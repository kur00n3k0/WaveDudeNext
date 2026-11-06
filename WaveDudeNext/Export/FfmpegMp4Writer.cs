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

using System.Diagnostics;
using System.Globalization;
using NLog;

namespace WaveDudeNext.Export;

/// <summary>
/// Writes H.264 MP4 through the system's <c>ffmpeg</c> program outside Windows (2026-11-06, the user's choice: a separate
/// process, so no codec library is linked and its license binds nothing here). The frames are converted here into NV12
/// with <see cref="YuvPlanes"/>, BT.601 at studio range like the Media Foundation path's, padded to even sizes by
/// repeating the last row and column as there, and piped raw into ffmpeg's standard input; ffmpeg only encodes, its
/// stream tagged with that matrix and range so players do not guess. The encoder is the first of
/// <see cref="Candidates"/> that this ffmpeg has and that encodes a test picture of the size (hardware first, as Windows
/// allows the card's encoder; libx264, then OpenH264 when no card takes it), with the quality 1..100 as each encoder's
/// constant-quality parameter, no B-frames and a one-second GOP so the file scrubs cleanly. One thread at a time.
/// </summary>
public sealed class FfmpegMp4Writer : IFrameSink
{
	private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

	/// <summary>Lines of ffmpeg's error output kept for an error message.</summary>
	private const int ErrorLines = 12;

	/// <summary>The H.264 encoders tried, in order: the cards' first, then the CPU's.</summary>
	public static readonly string[] Candidates = ["h264_nvenc", "h264_vaapi", "h264_qsv", "h264_amf", "libx264", "libopenh264"];

	/// <summary>The render node a VAAPI encoder is opened on.</summary>
	private const string VaapiDevice = "/dev/dri/renderD128";

	private static readonly Lazy<string?> ProgramPath = new(FindProgram);
	private static readonly Lazy<HashSet<string>> Encoders = new(ListEncoders);
	private static readonly Dictionary<(int Width, int Height, int Fps), string?> Chosen = [];
	private static readonly Lock ChosenLock = new();

	private readonly int _encodedWidth;
	private readonly int _encodedHeight;
	private readonly int[]? _padded;
	private readonly YuvPlanes _planes;
	private readonly byte[] _nv12;
	private readonly Process _process;
	private readonly Stream _input;
	private readonly Queue<string> _errors = new();
	private bool _closed;

	/// <param name="path">Output .mp4 path; an existing file is replaced.</param>
	/// <param name="width">Frame width in pixels.</param>
	/// <param name="height">Frame height in pixels.</param>
	/// <param name="framesPerSecond">Playback frame rate.</param>
	/// <param name="quality">1..100, mapped to the encoder's constant-quality parameter (<see cref="QuantiserOf"/>).</param>
	/// <exception cref="NotSupportedException">No ffmpeg on the PATH, the size is below 16 × 16, or no encoder of this
	/// ffmpeg takes the size.</exception>
	/// <exception cref="IOException">ffmpeg could not be started.</exception>
	public FfmpegMp4Writer(string path, int width, int height, int framesPerSecond, int quality)
	{
		Width = width;
		Height = height;
		Output = path;
		Quality = Math.Clamp(quality, 1, 100);
		_encodedWidth = (width + 1) & ~1;
		_encodedHeight = (height + 1) & ~1;
		if (_encodedWidth < 16 || _encodedHeight < 16)
		{
			throw new NotSupportedException("The picture is too small for H.264 video (16 × 16 at least).");
		}
		string program = ProgramPath.Value ?? throw new NotSupportedException("MP4 export needs ffmpeg, which is not on the PATH.");
		int fps = Math.Max(1, framesPerSecond);
		Encoder = EncoderFor(program, _encodedWidth, _encodedHeight, fps)
			?? throw new NotSupportedException($"No H.264 encoder of this ffmpeg takes a picture of {_encodedWidth} × {_encodedHeight}.");
		if (_encodedWidth != width || _encodedHeight != height)
		{
			_padded = new int[_encodedWidth * _encodedHeight];
		}
		_planes = new YuvPlanes(_encodedWidth, _encodedHeight, studioRange: true, subsampled: true);
		_nv12 = new byte[_encodedWidth * _encodedHeight * 3 / 2];
		List<string> arguments = Arguments(Encoder, ["-f", "rawvideo", "-pix_fmt", "nv12", "-s", Size, "-r", Rate(fps), "-i", "-"], fps, Quality, Bitrate(fps));
		arguments.AddRange(["-f", "mp4", "-y", path]);
		var info = new ProcessStartInfo(program) { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
		foreach (string argument in arguments)
		{
			info.ArgumentList.Add(argument);
		}
		try
		{
			_process = Process.Start(info) ?? throw new IOException("ffmpeg did not start.");
		}
		catch (System.ComponentModel.Win32Exception ex)
		{
			throw new IOException("ffmpeg could not be started: " + ex.Message, ex);
		}
		_process.ErrorDataReceived += (_, e) =>
		{
			if (e.Data is { Length: > 0 } line)
			{
				lock (_errors)
				{
					_errors.Enqueue(line);
					while (_errors.Count > ErrorLines)
					{
						_errors.Dequeue();
					}
				}
			}
		};
		_process.BeginErrorReadLine();
		_input = _process.StandardInput.BaseStream;
		Logger.Debug("MP4 by ffmpeg: {Encoder}, {Width} x {Height} at {Fps} frame/s, quality {Quality}; {Program} {Arguments}", Encoder, _encodedWidth, _encodedHeight, fps, Quality, program, string.Join(' ', arguments));
	}

	/// <summary>Whether MP4 can be written here: an ffmpeg on the PATH with at least one of the <see cref="Candidates"/>.</summary>
	public static bool IsAvailable() => ProgramPath.Value != null && Candidates.Any(Encoders.Value.Contains);

	/// <summary>The candidates this ffmpeg has, in the order they are tried (empty without ffmpeg), for the log and About.</summary>
	public static IReadOnlyList<string> AvailableEncoders() => ProgramPath.Value == null ? [] : [.. Candidates.Where(Encoders.Value.Contains)];

	/// <summary>The ffmpeg program found on the PATH; null when there is none.</summary>
	public static string? Program => ProgramPath.Value;

	/// <inheritdoc/>
	public int Width { get; }

	/// <inheritdoc/>
	public int Height { get; }

	/// <inheritdoc/>
	public int FrameCount { get; private set; }

	/// <inheritdoc/>
	public string Output { get; }

	/// <summary>The quality 1..100 the writer was given.</summary>
	public int Quality { get; }

	/// <summary>The ffmpeg encoder writing the file ("h264_nvenc", "libx264").</summary>
	public string Encoder { get; }

	/// <summary>
	/// The constant-quality parameter of a quality 1..100: 51 − 0.4 × quality, rounded, the scale every H.264 encoder's
	/// quantiser shares (0..51): 23 at 70, x264's default, 11 at 100, 51 at the bottom.
	/// </summary>
	public static int QuantiserOf(int quality) => (int)Math.Round(51 - 0.4 * Math.Clamp(quality, 1, 100), MidpointRounding.AwayFromZero);

	/// <inheritdoc/>
	public void Add(ReadOnlySpan<int> pixels)
	{
		if (_closed)
		{
			throw new InvalidOperationException("The video file is already closed.");
		}
		if (pixels.Length < Width * Height)
		{
			throw new ArgumentException("The frame is smaller than the video.", nameof(pixels));
		}
		ReadOnlySpan<int> source = pixels[..(Width * Height)];
		if (_padded != null)
		{
			for (int y = 0; y < _encodedHeight; y++)
			{
				ReadOnlySpan<int> row = pixels.Slice(Math.Min(y, Height - 1) * Width, Width);
				Span<int> target = _padded.AsSpan(y * _encodedWidth, _encodedWidth);
				row.CopyTo(target);
				if (_encodedWidth > Width)
				{
					target[Width] = row[Width - 1];
				}
			}
			source = _padded;
		}
		_planes.Convert(source.ToArray());
		CopyNv12();
		try
		{
			_input.Write(_nv12);
		}
		catch (IOException ex)
		{
			// ffmpeg has ended: its own words say why
			_process.WaitForExit();
			throw new IOException("ffmpeg stopped taking frames (exit " + _process.ExitCode + "): " + ErrorText(), ex);
		}
		FrameCount++;
	}

	/// <inheritdoc/>
	public void Close()
	{
		if (_closed)
		{
			return;
		}
		_closed = true;
		try
		{
			_input.Dispose();
		}
		catch (IOException)
		{
			// ffmpeg has ended already; its exit code tells
		}
		_process.WaitForExit();
		int exit = _process.ExitCode;
		_process.Dispose();
		if (exit != 0)
		{
			throw new IOException("ffmpeg failed (exit " + exit + "): " + ErrorText());
		}
		Logger.Debug("MP4 by ffmpeg: {Frames} frames written to {Path}", FrameCount, Output);
	}

	/// <inheritdoc/>
	public void Dispose() => Close();

	private string Size => _encodedWidth.ToString(CultureInfo.InvariantCulture) + "x" + _encodedHeight.ToString(CultureInfo.InvariantCulture);

	private static string Rate(int fps) => fps.ToString(CultureInfo.InvariantCulture);

	/// <summary>The author's bitrate rule of the Media Foundation path, 0.01 × 50^(quality/100) bits per pixel, for an encoder that takes a bitrate alone (OpenH264).</summary>
	private long Bitrate(int fps) => Math.Clamp((long)Math.Round((double)_encodedWidth * _encodedHeight * fps * 0.01 * Math.Pow(50.0, Quality / 100.0)), 300_000L, 200_000_000L);

	private string ErrorText()
	{
		lock (_errors)
		{
			return _errors.Count == 0 ? "no message" : string.Join(" | ", _errors);
		}
	}

	/// <summary>The planes as NV12 into <see cref="_nv12"/>: the Y rows (the planes are padded to 16, the picture is not), then Cb and Cr interleaved at half resolution.</summary>
	private void CopyNv12()
	{
		int w = _encodedWidth;
		int h = _encodedHeight;
		Span<byte> target = _nv12;
		for (int y = 0; y < h; y++)
		{
			_planes.Y.AsSpan(y * _planes.PaddedWidth, w).CopyTo(target.Slice(y * w, w));
		}
		Span<byte> uv = target[(w * h)..];
		int chromaWidth = _planes.ChromaWidth;
		for (int y = 0; y < h / 2; y++)
		{
			ReadOnlySpan<byte> cb = _planes.Cb.AsSpan(y * chromaWidth, w / 2);
			ReadOnlySpan<byte> cr = _planes.Cr.AsSpan(y * chromaWidth, w / 2);
			Span<byte> row = uv.Slice(y * w, w);
			for (int x = 0; x < w / 2; x++)
			{
				row[2 * x] = cb[x];
				row[2 * x + 1] = cr[x];
			}
		}
	}

	/// <summary>
	/// The command line for <paramref name="encoder"/> after the given input: the device and upload a VAAPI encoder
	/// needs, the encoder's constant-quality parameter, no B-frames, a GOP per second, 4:2:0, the BT.601 matrix at
	/// studio range the planes are in.
	/// </summary>
	private static List<string> Arguments(string encoder, string[] input, int fps, int quality, long bitrate)
	{
		string q = QuantiserOf(quality).ToString(CultureInfo.InvariantCulture);
		var arguments = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
		if (encoder == "h264_vaapi")
		{
			arguments.AddRange(["-vaapi_device", VaapiDevice]);
		}
		arguments.AddRange(input);
		arguments.AddRange(encoder switch
		{
			"h264_nvenc" => ["-c:v", encoder, "-rc", "vbr", "-cq", q, "-b:v", "0"],
			"h264_vaapi" => ["-vf", "format=nv12,hwupload", "-c:v", encoder, "-rc_mode", "CQP", "-qp", q],
			"h264_qsv" => ["-c:v", encoder, "-global_quality", q],
			"h264_amf" => ["-c:v", encoder, "-rc", "cqp", "-qp_i", q, "-qp_p", q],
			"libopenh264" => ["-c:v", encoder, "-b:v", bitrate.ToString(CultureInfo.InvariantCulture)],
			_ => ["-c:v", encoder, "-crf", q],
		});
		if (encoder != "h264_vaapi")
		{
			arguments.AddRange(["-pix_fmt", "yuv420p"]);
		}
		arguments.AddRange(["-bf", "0", "-g", Rate(fps), "-colorspace", "smpte170m", "-color_range", "tv"]);
		return arguments;
	}

	/// <summary>
	/// The first candidate that encodes two frames of the size, by a test run on a generated picture (a card's encoder
	/// may refuse a size, or the card may be missing though ffmpeg was built with its encoder); remembered per size and
	/// rate for the process, logged.
	/// </summary>
	private static string? EncoderFor(string program, int width, int height, int fps)
	{
		lock (ChosenLock)
		{
			if (Chosen.TryGetValue((width, height, fps), out string? known))
			{
				return known;
			}
			string size = width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture);
			string? chosen = null;
			var refused = new List<string>();
			foreach (string encoder in Candidates.Where(Encoders.Value.Contains))
			{
				List<string> arguments = Arguments(encoder, ["-f", "lavfi", "-i", "color=c=gray:s=" + size + ":r=" + Rate(fps), "-frames:v", "2"], fps, 70, 1_000_000);
				arguments.AddRange(["-f", "null", "-"]);
				(int exit, string errors, _) = Run(program, arguments, TimeSpan.FromSeconds(20));
				if (exit == 0)
				{
					chosen = encoder;
					break;
				}
				refused.Add(encoder + " (" + (errors.Length > 0 ? errors : "exit " + exit) + ")");
			}
			Logger.Debug("MP4 by ffmpeg at {Size}: {Encoder}{Refused}", size, chosen ?? "no encoder", refused.Count > 0 ? "; refused: " + string.Join("; ", refused) : "");
			Chosen[(width, height, fps)] = chosen;
			return chosen;
		}
	}

	/// <summary>Runs ffmpeg to its end; its exit code (−1 when it did not end in time and was killed), its error output on one line and its first line of output.</summary>
	private static (int Exit, string Errors, string FirstLine) Run(string program, List<string> arguments, TimeSpan timeout)
	{
		var info = new ProcessStartInfo(program) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
		foreach (string argument in arguments)
		{
			info.ArgumentList.Add(argument);
		}
		try
		{
			using Process process = Process.Start(info)!;
			Task<string> errors = process.StandardError.ReadToEndAsync();
			Task<string> output = process.StandardOutput.ReadToEndAsync();
			if (!process.WaitForExit(timeout))
			{
				process.Kill(entireProcessTree: true);
				process.WaitForExit();
				return (-1, "did not finish in " + timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s", "");
			}
			string firstLine = output.Result.Split('\n', 2)[0].Trim();
			return (process.ExitCode, string.Join(" | ", errors.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)), firstLine);
		}
		catch (System.ComponentModel.Win32Exception ex)
		{
			return (-1, ex.Message, "");
		}
	}

	/// <summary>The first executable file named <c>ffmpeg</c> in the PATH's folders; logged with its version line.</summary>
	private static string? FindProgram()
	{
		string name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
		foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
		{
			string candidate = Path.Combine(folder.Trim(), name);
			if (File.Exists(candidate) && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0))
			{
				(int exit, _, string version) = Run(candidate, ["-version"], TimeSpan.FromSeconds(10));
				Logger.Debug("ffmpeg: {Path}, {Version}", candidate, exit == 0 ? version : "exit " + exit);
				return candidate;
			}
		}
		Logger.Debug("ffmpeg: none on the PATH");
		return null;
	}

	/// <summary>The video encoders this ffmpeg lists (<c>-encoders</c>), by name; empty without ffmpeg.</summary>
	private static HashSet<string> ListEncoders()
	{
		var names = new HashSet<string>(StringComparer.Ordinal);
		if (ProgramPath.Value is not string program)
		{
			return names;
		}
		var info = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
		foreach (string argument in new[] { "-hide_banner", "-encoders" })
		{
			info.ArgumentList.Add(argument);
		}
		try
		{
			using Process process = Process.Start(info)!;
			Task<string> errors = process.StandardError.ReadToEndAsync();
			// " V....D libx264              libx264 H.264 / AVC ...": the flags, then the name
			foreach (string line in process.StandardOutput.ReadToEnd().Split('\n'))
			{
				string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
				if (fields.Length >= 2 && fields[0].Length == 6 && fields[0][0] == 'V')
				{
					names.Add(fields[1]);
				}
			}
			process.WaitForExit();
			errors.Wait();
		}
		catch (System.ComponentModel.Win32Exception)
		{
			// listed nothing
		}
		Logger.Debug("ffmpeg's H.264 encoders: {Encoders}", string.Join(", ", Candidates.Where(names.Contains).DefaultIfEmpty("none")));
		return names;
	}
}
