using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Utf8Json;
using Dapper;

namespace VivistaServer
{
	public enum InteractionType
	{
		None,
		Text,
		Image,
		Video,
		MultipleChoice,
		Audio,
		FindArea,
		MultipleChoiceArea,
		MultipleChoiceImage,
		TabularData,
		Chapter
	}

	public class QuestionResult
	{
		public InteractionType type;
		public int interactionId;
		public int answerChosen;
		public int correctAnswer;
		//NOTE(Simon): In case of Find Area
		public int wrongAnswersTried;
	}

	public class JsonArrayWrapper<T>
	{
		public T[] array;
	}

	public class AnalyticsController
	{
		[Route("POST", "/api/video_result")]
		[Route("POST", "/api/v1/video_result")]
		private static async Task VideoResultPost(HttpContext context)
		{
			if (!Guid.TryParse(context.Request.Query["id"], out var id))
			{
				await CommonController.Write404(context);
				return;
			}

			var connection = Database.OpenNewConnection();

			if (await VideoController.VideoExists(id, connection, context))
			{
				var videoData = JsonSerializer.Deserialize<JsonArrayWrapper<QuestionResult>>(context.Request.Form["0"]).array;

			}
		}

		[Route("POST", "/api/video_view_result")]
		[Route("POST", "/api/v1/video_view_result")]
		private static async Task VideoViewResultPost(HttpContext context)
		{
			if (!Guid.TryParse(context.Request.Query["id"], out var videoId))
			{
				await CommonController.Write404(context);
				return;
			}

			var connection = Database.OpenNewConnection();

			if (await VideoController.VideoExists(videoId, connection, context))
			{
				int[] viewData = JsonSerializer.Deserialize<JsonArrayWrapper<int>>(context.Request.Form["0"]).array;

				var transaction = await connection.BeginTransactionAsync();
				byte[] encodedHistogram = await Database.QuerySingleAsync<byte[]>(connection, "SELECT histogram FROM video_view_data WHERE videoid=@videoId", context, new { videoId }, transaction);
				int[] histogram = new int[encodedHistogram.Length / sizeof(int)];
				Buffer.BlockCopy(encodedHistogram, 0, histogram, 0, encodedHistogram.Length);

				for (int i = 0; i < viewData.Length; i++)
				{
					histogram[i] += viewData[i];
				}

				Buffer.BlockCopy(histogram, 0, encodedHistogram, 0, encodedHistogram.Length);
			
				await Database.ExecuteAsync(connection, "UPDATE video_view_data SET histogram=@encodedHistogram WHERE videoid=@videoId", context, encodedHistogram, transaction);
				await transaction.CommitAsync();
			}
		}

		private static int[] DecodeVideoViewData(byte[] toDecode)
		{
			int[] result = new int[toDecode.Length / sizeof(int)];
			Buffer.BlockCopy(toDecode, 0, result, 0, toDecode.Length);

			return result;
		}

		private static byte[] EncodeVideoViewData(int[] toEncode)
		{
			byte[] result = new byte[toEncode.Length * sizeof(int)];
			Buffer.BlockCopy(toEncode, 0, result, 0, result.Length);
			
			return result;
		}
	}
}
