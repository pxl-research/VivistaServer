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

		private static bool isProcessing = false;
		private const int queueLimit = 10;

		public static bool Add(string path)
		{
			if (File.Exists(path) && videoQueue.Count <= queueLimit)
			{
				var file = new FileInfo(path);
				videoQueue.Enqueue(file);
				return true;
			}

			Debug.WriteLine("[VideoEncoder]: Could not find video file.");
			return false;
		}

		public static int Count()
		{
			return videoQueue.Count;
		}

		public static async void ProcessVideoAsync()
		{
			if (!isProcessing)
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
				Debug.WriteLine("[VideoEncoder]: Ffmpeg binary not found.");
				return;
			}

			ffmpeg.Progress += OnTranscodeProgress;
			ffmpeg.Complete += OnTranscodeComplete;
			ffmpeg.Error += FfmpegOnError;

			var outputPath = Path.Combine($"{input.DirectoryName}", "main");

			//TODO (Jeroen): GPU info if nvidia acceleration is used.

			Debug.WriteLine($"[VideoEncoder]: Started transcoding {input.Name}.");
			var command =
				$"-y -i {input.FullName} -filter_complex \"[0:v]format=pix_fmts=yuv420p,split=3[in1][in2][in3];[in1]scale=1920:-1[out1];[in2]scale=2732:-1[out2];[in3]scale=4096:-1[out3]\"" +
				$" -map \"[out1]\" -preset medium -vsync passthrough -an -c:v libx264 -crf 18 -b:v 10M -maxrate 15M -bufsize 30M -f mp4 {outputPath}_1080.mp4" +
				$" -map \"[out2]\" -preset medium -vsync passthrough -an -c:v libx264 -crf 18 -b:v 15M -maxrate 30M -bufsize 60M -f mp4 {outputPath}_1440.mp4" +
				$" -map \"[out3]\" -preset medium -vsync passthrough -an -c:v libx264 -crf 18 -b:v 20M -maxrate 40M -bufsize 80M -f mp4 {outputPath}_2160.mp4";
			Debug.WriteLine(command);
			await ffmpeg.ExecuteAsync(command);
		}

		private static void FfmpegOnError(object sender, ConversionErrorEventArgs e)
		{
			Debug.WriteLine("[VideoEncoder]: Error " + e.Exception.Message);
		}

		private static void OnTranscodeProgress(object sender, ConversionProgressEventArgs e)
		{
			Debug.WriteLine("[VideoEncoder]: Processing: {0}.", e.ProcessedDuration);
		}

		private static void OnTranscodeComplete(object sender, ConversionCompleteEventArgs e)
		{
			Debug.WriteLine("[VideoEncoder]: Completed transcoding.");
			ProcessVideoAsync();
			//TODO: (Jeroen) Check if files are playable
		}
	}
}
