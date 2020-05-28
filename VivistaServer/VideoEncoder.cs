using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FFmpeg.NET;
using FFmpeg.NET.Events;

namespace VivistaServer
{
	public abstract class VideoEncoder
	{
		private static ConcurrentQueue<FileInfo> videoQueue = new ConcurrentQueue<FileInfo>();
		private static long frames;

		private static bool isProcessing = false;

		public static async void EncodeAsync(string path)
		{
			if (File.Exists(path))
			{
				var file = new FileInfo(path);
				videoQueue.Enqueue(file);
				if (!isProcessing)
				{
					await ProcessVideoAsync();
				}
			}
			else
			{
				// TODO (Jeroen): return error
				Debug.WriteLine("[VideoEncoder]: Could not find video file");
				return;
			}
		}

		private static async Task ProcessVideoAsync()
		{
			FileInfo video = null;
			var hasVideo = videoQueue.TryDequeue(out video);
			if (hasVideo)
			{
				isProcessing = true;
				await TranscodeVideoAsync(video);
			}
			else
			{
				isProcessing = false;
			}
		}

		private static async Task TranscodeVideoAsync(FileInfo input)
		{
			Engine ffmpeg = null;

			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				ffmpeg = new Engine(@"..\bin\ffmpeg\v4\ffmpeg.exe");
			}
			else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
			{
				//TODO (Jeroen): Needs to check supported version
				ffmpeg = new Engine(@"/user/local/bin/ffmpeg");
			}
			else
			{
				Debug.WriteLine("[VideoEncoder]: Ffmpeg binary not found");
				return;
			}

			ffmpeg.Progress += OnTranscodeProgress;
			ffmpeg.Complete += OnTranscodeComplete;
			ffmpeg.Error += FfmpegOnError;

			var outputPath = Path.Combine($"{input.DirectoryName}", "main");

			Debug.WriteLine($"[VideoEncoder]: Started transcoding {input.FullName}");
			var command =
				$"-y -i {input.FullName} -filter_complex \"[0:v]format=pix_fmts=yuv420p,split=3[s1][s2][s3]\"" +
				$" -map \"[s1]\" -preset slow -vsync passthrough -an -c:v libx264 -crf 20 -b:v 10M -maxrate 15M -bufsize 30M -f mp4 {outputPath}_10.mp4" +
				$" -map \"[s2]\" -preset slow -vsync passthrough -an -c:v libx264 -crf 20 -b:v 15M -maxrate 30M -bufsize 60M -f mp4 {outputPath}_15.mp4" +
				$" -map \"[s3]\" -preset slow -vsync passthrough -an -c:v libx264 -crf 20 -b:v 20M -maxrate 40M -bufsize 80M -f mp4 {outputPath}_20.mp4";
			Debug.WriteLine(command);
			await ffmpeg.ExecuteAsync(command);
		}

		private static void FfmpegOnError(object sender, ConversionErrorEventArgs e)
		{
			Debug.WriteLine("[VideoEncoder]: Error " + e.Exception.Message);
		}

		private static void OnTranscodeProgress(object sender, ConversionProgressEventArgs e)
		{
			Debug.WriteLine("[VideoEncoder]: Processing: {0}., e.ProcessedDuration);
		}

		private static void OnTranscodeComplete(object sender, ConversionCompleteEventArgs e)
		{
			Debug.WriteLine("[VideoEncoder]: Completed transcoding");
			_ = ProcessVideoAsync();
		}
	}
}
