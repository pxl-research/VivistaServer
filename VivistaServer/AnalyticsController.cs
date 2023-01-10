using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Utf8Json;

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

	public class Question
	{
		public InteractionType type;
		public int interactionId;
		public int correctAnswer;
		//NOTE(Simon): In case of Find Area
	}

	public class UserAnswer
	{
		DateTime timestamp;
		Guid questionId;
		int userId;
		int totalScore;
	}

	public class Answer
	{
		public Guid questionId;
		public int userAnswerId;
		public int answerData;
		public int score;
	}

	public class JsonArrayWrapper<T>
	{
		public T[] array;
	}

	public class AnalyticsController
	{
		private const int VIDEO_VIEW_HISTOGRAM_INTS = 100;
		private const int VIDEO_VIEW_HISTOGRAM_BYTES = VIDEO_VIEW_HISTOGRAM_INTS * 4;

		[Route("POST", "/api/video_result")]
		[Route("POST", "/api/v1/video_result")]
		private static async Task VideoResultPost(HttpContext context)
		{
			if (!Guid.TryParse(context.Request.Query["id"], out var id))
			{
				await CommonController.Write404(context);
				return;
			}

			using var connection = Database.OpenNewConnection();
			using var transaction = connection.BeginTransaction();

			if (await VideoController.VideoExists(id, connection, context))
			{
				var questions = await GetQuestionsForVideo(id, connection, context, transaction);
				if (questions.Count == 0)
				{
					questions = ExtractQuestionsFromVideo(id);
					await WriteQuestions(id, questions, connection, context, transaction);
				}

				var answers = JsonSerializer.Deserialize<JsonArrayWrapper<Answer>>(context.Request.Form["answers"]).array;

				string userIdString = context.Request.Form["userid"].ToString();
				bool isAnonymous = String.IsNullOrEmpty(userIdString);
				Int32.TryParse(userIdString, out int userId);

				await Database.ExecuteAsync(connection, "INSERT INTO user_answers VALUES (@timestamp, @questionId, @userId, @total_score)", context, new UserAnswer())
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

			using var connection = Database.OpenNewConnection();

			if (await VideoController.VideoExists(videoId, connection, context))
			{
				var values = context.Request.Form["values"];
				int[] viewData = JsonSerializer.Deserialize<JsonArrayWrapper<int>>(values).array;

				if (viewData.Length != VIDEO_VIEW_HISTOGRAM_INTS)
				{
					await CommonController.WriteError(context, $"Length of submitted view histogram should be exactly {VIDEO_VIEW_HISTOGRAM_INTS}.", StatusCodes.Status400BadRequest);
					return;
				}

				var transaction = await connection.BeginTransactionAsync();

				byte[] encodedHistogram = await Database.QuerySingleOrDefaultAsync<byte[]>(connection, "SELECT histogram FROM video_view_data WHERE videoid=@videoId", context, new { videoId }, transaction);

				//NOTE(Simon): Create histogram bytes if it doesn't exist yet or is corrupted
				if (encodedHistogram == null || encodedHistogram.Length != VIDEO_VIEW_HISTOGRAM_BYTES)
				{
					encodedHistogram = new byte[VIDEO_VIEW_HISTOGRAM_BYTES];
				}

				int[] histogram = new int[encodedHistogram.Length / sizeof(int)];
				Buffer.BlockCopy(encodedHistogram, 0, histogram, 0, encodedHistogram.Length);

				for (int i = 0; i < viewData.Length; i++)
				{
					histogram[i] += viewData[i];
				}

				Buffer.BlockCopy(histogram, 0, encodedHistogram, 0, encodedHistogram.Length);

				try
				{
					await Database.ExecuteAsync(connection, @"INSERT INTO video_view_data (videoid, histogram)
																VALUES (@videoId, @encodedHistogram)
																ON CONFLICT(videoid) DO UPDATE 
																SET histogram=@encodedHistogram", 
																context, new { encodedHistogram, videoId }, transaction);
				}
				catch (Exception e)
				{
					CommonController.LogDebug(e.ToString());
					await transaction.RollbackAsync();
					await CommonController.WriteError(context, "Something went wrong while storing view data", StatusCodes.Status500InternalServerError, e);
					return;
				}

				await transaction.CommitAsync();
			}
		}



		private static async Task<List<Question>> GetQuestionsForVideo(Guid videoid, NpgsqlConnection connection, HttpContext context, NpgsqlTransaction transaction = null)
		{
			var questions = await Database.QueryAsync<Question>(connection, "SELECT * FROM questions WHERE video_id = @videoid", context, new { videoid });
			return questions.ToList();
		}

		private static List<Question> ExtractQuestionsFromVideo(Guid videoid)
		{
			string metaPath = Path.Combine(VideoController.baseFilePath, videoid.ToString(), "meta.json");

			//TODO(Simon): Add interactionpoint parsing to this function.
			var meta = VideoController.ReadMetaFile(metaPath);

			return meta.questions;
		}

		private static async Task<bool> WriteQuestions(Guid videoid, List<Question> questions, NpgsqlConnection connection, HttpContext context, NpgsqlTransaction transaction = null)
		{
			try
			{
				int result = await Database.ExecuteAsync(connection,
						@"INSERT INTO questions (id, videoid, question, questiontype, potential_answers, correct_answer)
						VALUES (@id, @videoid, @question, @questiontype, @potential_Answers, correct_answer)
						ON CONFLICT(id) DO UPDATE
						SET id=@id, videoid=@videoid, question=@question, questiontype=@questiontype, potentialanswers=@potentialanswers, correct_answer=@correct_answer",
						context, questions, transaction);

				return result > 0;
			}
			catch (Exception e)
			{
				CommonController.LogDebug(e.ToString());
				return false;
			}
		}
	}
}
