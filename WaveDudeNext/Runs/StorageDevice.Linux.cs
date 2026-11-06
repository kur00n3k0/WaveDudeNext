// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alexander Graef <alex@graef.cc>
//
// This file is part of WaveDude Next, a 2D wave simulator.
//
// Based on the work of
//   Jeroen Vleggaar (Huygens Optics), author of Wave Dude 2D, Copyright (C) 2026
//     <https://www.youtube.com/@HuygensOptics>, <https://www.huygensoptics.com>
//   0x23 (Diffraction Limited), author of WaveSimulator2D
//     <https://www.youtube.com/@DiffractionLimited>, <https://github.com/0x23/WaveSimulator2D>
//
// WaveDude Next is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. It is distributed WITHOUT ANY WARRANTY. See the LICENSE.md file or
// <https://www.gnu.org/licenses/>.

using System.Globalization;
using System.Text;

namespace WaveDudeNext.Runs;

/// <summary>
/// The Linux answer of <see cref="IncursSeekPenalty"/> (2026-11-06): the mount holding the path from
/// <c>/proc/self/mountinfo</c> (the longest mount point that contains the path's nearest existing folder, its links
/// resolved), its block device by number under <c>/sys/dev/block</c>, or by the mount's source when the file system
/// reports a device number of its own (btrfs), then the kernel's <c>queue/rotational</c> flag: of the device itself for a
/// disk, of its disk for a partition, of every device beneath it for a stacked one (LVM, dm-crypt, software RAID), where a
/// hard disk among them is a seek penalty as on Windows. Read-only files of the kernel, no privileges needed.
/// </summary>
public static partial class StorageDevice
{
	/// <summary>Device-stacking depth at which the walk gives up (a loop in sysfs cannot happen, a deep stack can).</summary>
	private const int MaxStackDepth = 8;

	/// <summary>
	/// The mount point holding <paramref name="path"/> on Linux ("/", "/home"), for a log line; null elsewhere, for a
	/// relative path or when the mount table cannot be read.
	/// </summary>
	public static string? MountPoint(string path) => OperatingSystem.IsLinux() && Path.IsPathRooted(path) ? FindMount(path)?.MountPoint : null;

	private static bool? LinuxSeekPenalty(string path)
	{
		// a relative path names no particular mount, as on Windows
		if (!Path.IsPathRooted(path))
		{
			return null;
		}
		Mount? mount = FindMount(path);
		if (mount == null)
		{
			return null;
		}
		string byNumber = "/sys/dev/block/" + mount.Device;
		if (Directory.Exists(byNumber))
		{
			return Rotational(Resolve(byNumber), 0);
		}
		// a file system with a device number of its own (btrfs): its source names the block device
		if (mount.Source.StartsWith("/dev/", StringComparison.Ordinal) && File.Exists(mount.Source))
		{
			string byName = "/sys/class/block/" + Path.GetFileName(Resolve(mount.Source));
			return Directory.Exists(byName) ? Rotational(Resolve(byName), 0) : null;
		}
		return null;
	}

	/// <summary>
	/// The seek penalty of the block device whose sysfs folder is <paramref name="device"/>: a stacked device answers
	/// for the devices beneath it (true when any is a hard disk, null when any cannot say), a partition for its disk,
	/// a disk by its flag.
	/// </summary>
	private static bool? Rotational(string device, int depth)
	{
		if (depth > MaxStackDepth)
		{
			return null;
		}
		string slaves = Path.Combine(device, "slaves");
		string[] beneath = Directory.Exists(slaves) ? Directory.GetFileSystemEntries(slaves) : [];
		if (beneath.Length > 0)
		{
			bool? result = false;
			foreach (string slave in beneath)
			{
				bool? penalty = Rotational(Resolve(slave), depth + 1);
				if (penalty == true)
				{
					return true;
				}
				if (penalty == null)
				{
					result = null;
				}
			}
			return result;
		}
		if (File.Exists(Path.Combine(device, "partition")))
		{
			string? disk = Path.GetDirectoryName(device);
			return disk == null ? null : Rotational(disk, depth + 1);
		}
		string flag = Path.Combine(device, "queue", "rotational");
		if (!File.Exists(flag))
		{
			return null;
		}
		return File.ReadAllText(flag).Trim() switch
		{
			"1" => true,
			"0" => false,
			_ => null,
		};
	}

	/// <summary>A line of <c>/proc/self/mountinfo</c>: the device number ("259:3"), the mount point and the source ("/dev/nvme0n1p3").</summary>
	private sealed record Mount(string Device, string MountPoint, string Source);

	/// <summary>The mount holding <paramref name="path"/>'s nearest existing folder; null when the table cannot be read.</summary>
	private static Mount? FindMount(string path)
	{
		string target;
		IEnumerable<string> lines;
		try
		{
			target = Resolve(NearestExisting(Path.GetFullPath(path)));
			lines = File.ReadAllLines("/proc/self/mountinfo");
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			return null;
		}
		Mount? best = null;
		foreach (string line in lines)
		{
			// "36 35 98:0 /mnt1 /mnt/parent rw,noatime master:1 - ext3 /dev/root rw": fields 3 and 5, the source after " - "
			string[] fields = line.Split(' ');
			int separator = Array.IndexOf(fields, "-");
			if (fields.Length < 5 || separator < 0 || separator + 2 >= fields.Length)
			{
				continue;
			}
			string mountPoint = Unescape(fields[4]);
			// the last mount of the longest point wins: a later mount over the same point hides the earlier one
			if (Contains(mountPoint, target) && (best == null || mountPoint.Length >= best.MountPoint.Length))
			{
				best = new Mount(fields[2], mountPoint, Unescape(fields[separator + 2]));
			}
		}
		return best;
	}

	/// <summary>Whether <paramref name="path"/> lies at or below <paramref name="mountPoint"/>, by whole components.</summary>
	private static bool Contains(string mountPoint, string path)
		=> mountPoint == "/" || path == mountPoint || path.StartsWith(mountPoint + "/", StringComparison.Ordinal);

	/// <summary>The mount table's octal escapes undone ("\040" a space, "\011" a tab, "\012" a line feed, "\134" a backslash).</summary>
	private static string Unescape(string field)
	{
		if (!field.Contains('\\', StringComparison.Ordinal))
		{
			return field;
		}
		var text = new StringBuilder(field.Length);
		for (int i = 0; i < field.Length; i++)
		{
			if (field[i] == '\\' && i + 3 < field.Length && int.TryParse(field.AsSpan(i + 1, 3), NumberStyles.None, CultureInfo.InvariantCulture, out _))
			{
				text.Append((char)Convert.ToInt32(field.Substring(i + 1, 3), 8));
				i += 3;
			}
			else
			{
				text.Append(field[i]);
			}
		}
		return text.ToString();
	}

	/// <summary>The path itself when it exists, else its nearest existing ancestor ("/" at the latest).</summary>
	private static string NearestExisting(string path)
	{
		string current = path;
		while (!Directory.Exists(current) && !File.Exists(current))
		{
			current = Path.GetDirectoryName(current) ?? "/";
		}
		return current;
	}

	/// <summary>An existing absolute path with the links among its components resolved, component by component.</summary>
	private static string Resolve(string path)
	{
		string result = "/";
		foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
		{
			string next = Path.Combine(result, part);
			FileSystemInfo? link = new FileInfo(next).ResolveLinkTarget(returnFinalTarget: true);
			result = link == null ? next : Path.GetFullPath(link.FullName);
		}
		return result;
	}
}
