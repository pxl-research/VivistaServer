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

		public static async Task VideoViewResultPost(HttpContext context)
		{
			if (!Guid.TryParse(context.Request.Query["id"], out var id))
			{
				await CommonController.Write404(context);
				return;
			}

			var connection = Database.OpenNewConnection();

			if (await VideoController.VideoExists(id, connection, context))
			{
				int[] viewData = JsonSerializer.Deserialize<JsonArrayWrapper<int>>(context.Request.Form["0"]).array;

				var transaction = await connection.BeginTransactionAsync();
				int[] average = await connection.QuerySingleAsync<int[]>("", id, transaction);

				for (int i = 0; i < viewData.Length; i++)
				{
					average[i] += viewData[i];
				}

				await connection.ExecuteAsync("", average, transaction);
				await transaction.CommitAsync();
			}
		}

		private static int[] DecodeVideoViewData(string toDecode)
		{
			byte[] bytes = System.Text.Encoding.UTF8.GetBytes(toDecode);

			int[] result = new int[toDecode.Length / sizeof(int)];
			Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);

			return result;
		}

		private static string EncodeVideoViewData(int[] toEncode)
		{
			byte[] result = new byte[toEncode.Length * sizeof(int)];
			Buffer.BlockCopy(toEncode, 0, result, 0, result.Length);
			
			return System.Text.Encoding.UTF8.GetString(result);
		}
	}
}
