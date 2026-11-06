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

using System.Runtime.InteropServices;

namespace WaveDudeNext.Runs;

/// <summary>
/// How much memory a run's frames may take before the rest goes to disk (2026-10-14). The budget is decided
/// when a run starts, from what the machine has to spare at that moment: a share of the physical memory (the
/// user's setting), but never more than what is available now minus a margin for the rest of the application
/// and the system, so a run never pushes the machine into its page file. Windows answers through
/// <c>GlobalMemoryStatusEx</c>, Linux through <c>/proc/meminfo</c>; elsewhere the runtime's view of the total
/// serves, with half of it taken as available. Thread-free.
/// </summary>
public static class FrameMemory
{
	/// <summary>The share value meaning "no cap: whatever is free now minus the margin" (2026-10-15, the user's choice: the application decides, a percentage is only an upper bound).</summary>
	public const int Automatic = 0;

	/// <summary>Share of the physical memory a run may take when the user has not chosen one: <see cref="Automatic"/>.</summary>
	public const int DefaultPercent = Automatic;

	/// <summary>Memory left untouched below what is available: the application, the engines' own buffers and the system need room too.</summary>
	public const long Margin = 2L << 30;

	/// <summary>Bytes of physical memory in the machine.</summary>
	public static long PhysicalBytes()
	{
		if (OperatingSystem.IsWindows() && TryStatus(out MemoryStatus status))
		{
			return (long)Math.Min(status.TotalPhys, long.MaxValue);
		}
		return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
	}

	/// <summary>
	/// Bytes free for new allocations right now: the lesser of the free physical memory (the standby cache counts as
	/// free, as Windows reports it) and the commit still available (physical memory plus page file minus what every
	/// process has committed). Windows refuses any allocation at the commit limit, which with a small page file comes
	/// before the physical memory is used up: a full-HD run of 12,500 frames filled the budget by the physical
	/// measure alone and the preview's next bitmap then failed to allocate (2026-10-19).
	/// </summary>
	public static long AvailableBytes()
	{
		if (OperatingSystem.IsWindows() && TryStatus(out MemoryStatus status))
		{
			return (long)Math.Min(Math.Min(status.AvailPhys, status.AvailPageFile), long.MaxValue);
		}
		if (OperatingSystem.IsLinux() && TryLinuxAvailable(out long available))
		{
			return available;
		}
		return PhysicalBytes() / 2;
	}

	/// <summary>
	/// Linux's measure (2026-11-06): <c>MemAvailable</c>, the kernel's estimate of what new allocations can take without
	/// swapping, the reclaimable page cache included, as Windows counts its standby list; under strict overcommit
	/// (<c>vm.overcommit_memory</c> 2, where the kernel refuses an allocation at the commit limit as Windows does) no more
	/// than <c>CommitLimit</c> minus <c>Committed_AS</c>. In the default heuristic mode the commit figures bound nothing:
	/// pages are taken when touched, and the margin below what is available is what keeps the machine out of its swap.
	/// </summary>
	private static bool TryLinuxAvailable(out long available)
	{
		available = 0;
		long memAvailable = -1;
		long commitLimit = -1;
		long committed = -1;
		try
		{
			foreach (string line in File.ReadLines("/proc/meminfo"))
			{
				memAvailable = line.StartsWith("MemAvailable:", StringComparison.Ordinal) ? Kilobytes(line) : memAvailable;
				commitLimit = line.StartsWith("CommitLimit:", StringComparison.Ordinal) ? Kilobytes(line) : commitLimit;
				committed = line.StartsWith("Committed_AS:", StringComparison.Ordinal) ? Kilobytes(line) : committed;
			}
			bool strict = File.ReadAllText("/proc/sys/vm/overcommit_memory").Trim() == "2";
			if (memAvailable < 0)
			{
				return false;
			}
			available = strict && commitLimit >= 0 && committed >= 0 ? Math.Min(memAvailable, Math.Max(0, commitLimit - committed)) : memAvailable;
			return true;
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	/// <summary>The value of a <c>/proc/meminfo</c> line ("Name:   12345 kB") in bytes; -1 when it does not parse.</summary>
	private static long Kilobytes(string line)
	{
		string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		return parts.Length >= 2 && long.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long kilobytes) ? kilobytes * 1024 : -1;
	}

	/// <summary>
	/// The frame budget for a run started now: what is available minus <see cref="Margin"/>, and no more than
	/// <paramref name="percentOfPhysical"/> of the physical memory when that is not <see cref="Automatic"/>; never negative.
	/// </summary>
	/// <param name="percentOfPhysical">The user's upper bound, 1 to 100 (clamped), or <see cref="Automatic"/> for none.</param>
	/// <param name="reclaimedBytes">Memory the caller gives back before the new frames are allocated (the outgoing
	/// run's frames), counted as available: Windows still reports it as in use when asked now. Without it a re-run
	/// measured next to its predecessor's store under-budgets by that store's size and spills a run that fits
	/// (found on a 16 GB laptop, 2026-10-16).</param>
	public static long Budget(int percentOfPhysical, long reclaimedBytes = 0)
		=> Budget(AvailableBytes(), PhysicalBytes(), percentOfPhysical, reclaimedBytes);

	/// <summary>The arithmetic of <see cref="Budget(int, long)"/> on given measurements, exact for the harness.</summary>
	internal static long Budget(long availableBytes, long physicalBytes, int percentOfPhysical, long reclaimedBytes)
	{
		long byAvailable = availableBytes + reclaimedBytes - Margin;
		if (percentOfPhysical <= Automatic)
		{
			return Math.Max(0, byAvailable);
		}
		double share = Math.Clamp(percentOfPhysical, 1, 100) / 100.0;
		long byShare = (long)(physicalBytes * share);
		return Math.Max(0, Math.Min(byShare, byAvailable));
	}

	/// <summary>A byte count as gigabytes with one decimal and the unit, in the current culture, for the UI.</summary>
	public static string Gigabytes(long bytes) => ToGigabytes(bytes).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + " GB";

	/// <summary>A byte count in gigabytes (2^30 bytes), for a log line's <c>{Name:0.0} GB</c>, which NLog formats invariantly.</summary>
	public static double ToGigabytes(long bytes) => bytes / (double)(1L << 30);

	private static bool TryStatus(out MemoryStatus status)
	{
		status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
		return GlobalMemoryStatusEx(ref status);
	}

	/// <summary>The <c>MEMORYSTATUSEX</c> structure of <c>kernel32</c>.</summary>
	[StructLayout(LayoutKind.Sequential)]
	private struct MemoryStatus
	{
		public uint Length;
		public uint MemoryLoad;
		public ulong TotalPhys;
		public ulong AvailPhys;
		public ulong TotalPageFile;
		public ulong AvailPageFile;
		public ulong TotalVirtual;
		public ulong AvailVirtual;
		public ulong AvailExtendedVirtual;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
