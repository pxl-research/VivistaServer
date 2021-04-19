using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Dapper;
using Fluid;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using static VivistaServer.CommonController;

namespace VivistaServer
{
	public class Startup
	{
		public class Video
		{
			public Guid id;
			public int userid;
			public string username;
			public DateTime timestamp;
			public int downloadsize;

			public string title;
			public string description;
			public int length;
		}

		public class VideoResponse
		{
			public int totalcount;
			public int page;
			public int count;
			public IEnumerable<Video> videos;
		}

		public class Meta
		{
			public Guid guid;
			public string title;
			public string description;
			public int length;
		}

		private static Router router;


		private const int indexCountDefault = 10;

		private const string baseFilePath = @"C:\VivistaServerData\";

		public void ConfigureServices(IServiceCollection services)
		{
			services.Configure<FormOptions>(config =>
			{
				config.MultipartBodyLengthLimit = long.MaxValue;
			});

			router = new Router();

			EmailClient.InitCredentials();

			HTMLRenderer.RegisterLayout(BaseLayout.Web, "Templates/base.liquid");
		}

		public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
		{
			if (env.IsDevelopment())
			{
				app.UseDeveloperExceptionPage();
				CommonController.baseURL = "https://localhost:5001";
			}
			else
			{
				app.UseHsts();
				CommonController.baseURL = "https://vivista.net";
			}

			app.UseStaticFiles();

			rng = new RNGCryptoServiceProvider();
			Task.Run(PeriodicFunction);

			app.Run(async (context) =>
			{
				PrintDebugData(context);

				SetJSONContentType(context);

				await router.RouteAsync(context.Request, context);
			});
		}

		private void PrintDebugData(HttpContext context)
		{
#if DEBUG
			Console.WriteLine("Request data:");
			Console.WriteLine($"\tPath: {context.Request.Path}");
			Console.WriteLine($"\tMethod: {context.Request.Method}");
			Console.WriteLine("\tQuery: ");
			foreach (var kvp in context.Request.Query)
			{
				Console.WriteLine($"\t\t{kvp.Key}: {kvp.Value}");
			}
			Console.WriteLine("\tHeaders: ");
			foreach (var kvp in context.Request.Headers)
			{
				Console.WriteLine($"\t\t{kvp.Key}: {kvp.Value}");
			}
			if (!context.Request.HasFormContentType)
			{
				Console.WriteLine($"\tBody: {new StreamReader(context.Request.Body).ReadToEnd()}");
			}
#endif
		}

		private static async Task PeriodicFunction()
		{
			while (true)
			{
				Console.WriteLine("periodic");
				await Task.Delay(5000);
			}
		}




		[Route("GET", "/api/videos")]
		[Route("GET", "/api/v1/videos")]
		private static async Task VideosGet(HttpContext context)
		{
			var args = context.Request.Query;

			string author = args["author"].ToString().ToLowerInvariant().Trim();
			int? userid = null;
			DateTime? uploadDate;

			if (!Int32.TryParse(args["offset"].ToString(), out int offset) || offset < 0)
			{
				offset = 0;
			}

			if (!Int32.TryParse(args["count"].ToString(), out int count) || count > 100 || count < 0)
			{
				count = indexCountDefault;
			}

			using var connection = Database.OpenNewConnection();

			//TODO(Simon): Fuzzy search for username
			if (!String.IsNullOrEmpty(author))
			{
				userid = await UserController.GetUserIdFromUsername(author, connection);
			}

			if (!Int32.TryParse(args["agedays"].ToString(), out int daysOld) || daysOld < 0)
			{
				uploadDate = null;
			}
			else
			{
				uploadDate = DateTime.UtcNow.AddDays(-daysOld);
			}

			var videos = new VideoResponse();

			try
			{
				//TODO(Simon): There might be a faster way to get the count, while also executing just 1 query: add "count(*) OVER() AS total_count" to query
				videos.totalcount = await connection.QuerySingleAsync<int>(@"select count(*) from videos v
								inner join users u on v.userid = u.userid
								where (@userid::int is NULL or v.userid=@userid)
								and (@uploadDate::timestamp is NULL or v.timestamp>=@uploadDate)", new { userid, uploadDate });
				videos.videos = await connection.QueryAsync<Video>(@"select v.id, v.userid, u.username, v.timestamp, v.downloadsize, v.title, v.description, v.length from videos v
								inner join users u on v.userid = u.userid
								where (@userid::int is NULL or v.userid=@userid)
								and (@uploadDate::timestamp is NULL or v.timestamp>=@uploadDate)
								order by v.timestamp desc
								limit @count
								offset @offset", new { userid, uploadDate, count, offset });

				videos.count = videos.videos.AsList().Count;
				videos.page = videos.totalcount > 0 ? offset / videos.totalcount + 1 : 1;
				await context.Response.Body.WriteAsync(Utf8Json.JsonSerializer.SerializeUnsafe(videos));
			}
			catch (Exception e)
			{
				await WriteError(context, "Something went wrong while processing this request", StatusCodes.Status500InternalServerError, e);
			}
		}

		[Route("GET", "/api/video")]
		[Route("GET", "/api/v1/video")]
		private static async Task VideoGet(HttpContext context)
		{
			var args = context.Request.Query;
			var videoid = args["videoid"].ToString();

			if (String.IsNullOrEmpty(videoid))
			{
				await Write404(context);
				return;
			}

			using var connection = Database.OpenNewConnection();

			Video video;
			try
			{
				video = await connection.QuerySingleAsync<Video>(@"select v.id, v.userid, u.username, v.timestamp, v.downloadsize from videos v
													inner join users u on v.userid = u.userid
													where v.id=@videoid::uuid", new { videoid });

				if (video == null)
				{
					await Write404(context);
					return;
				}
			}
			catch (Exception e)
			{
				await WriteError(context, "Something went wrong while processing this request", StatusCodes.Status500InternalServerError, e);
				return;
			}

			var videoPath = $"{baseFilePath}{video.id}\\main.mp4";

			if (File.Exists(videoPath))
			{
				context.Response.ContentType = "video/mp4";
				try
				{
					await context.Response.SendFileAsync(videoPath);
				}
				catch (Exception e)
				{
					await WriteError(context, "Something went wrong while sending this file", StatusCodes.Status500InternalServerError, e);
				}
			}
			else
			{
				await Write404(context);
			}
		}

		[Route("POST", "/api/video")]
		[Route("POST", "/api/v1/video")]
		private static async Task VideoPost(HttpContext context)
		{
			context.Features.Get<IHttpMaxRequestBodySizeFeature>().MaxRequestBodySize = null;

			using var connection = Database.OpenNewConnection();

			var form = context.Request.Form;
			string token = form["token"];

			if (await UserController.AuthenticateToken(token, connection))
			{
				var guid = new Guid(form["uuid"]);
				string basePath = Path.Combine(baseFilePath, guid.ToString());
				string videoPath = Path.Combine(basePath, "main.mp4");
				string metaPath = Path.Combine(basePath, "meta.json");
				string thumbPath = Path.Combine(basePath, "thumb.jpg");

				try
				{
					Directory.CreateDirectory(basePath);
					using (var videoStream = new FileStream(videoPath, FileMode.OpenOrCreate))
					using (var metaStream = new FileStream(metaPath, FileMode.OpenOrCreate))
					using (var thumbStream = new FileStream(thumbPath, FileMode.OpenOrCreate))
					{
						var videoCopyOp = form.Files["video"].CopyToAsync(videoStream);
						var metaCopyOp = form.Files["meta"].CopyToAsync(metaStream);
						var thumbCopyOp = form.Files["thumb"].CopyToAsync(thumbStream);
						await Task.WhenAll(videoCopyOp, metaCopyOp, thumbCopyOp);
					}

					//TODO(Simon): Move all the file-reading and database code somewhere else. So that users don't have to reupload if database insert fails
					var metaTask = Task.Run(() => ReadMetaFile(metaPath));
					var userIdTask = UserController.GetUserIdFromToken(token, connection);
					Task.WaitAll(metaTask, userIdTask);

					var meta = metaTask.Result;
					int userId = userIdTask.Result;

					if (await UserOwnsVideo(guid, userId, connection))
					{
						var timestamp = DateTime.UtcNow;
						await connection.ExecuteAsync(@"update videos set (title, description, length, timestamp)
												= (@title, @description, @length, @timestamp)
												where id = @guid",
												new { guid, meta.title, meta.description, meta.length, timestamp });
					}
					else
					{
						await connection.ExecuteAsync(@"insert into videos (id, userid, title, description, length)
												values (@guid, @userid, @title, @description, @length)",
												new { guid, userid = userId, meta.title, meta.description, meta.length });
					}

					await context.Response.WriteAsync("{}");
				}
				catch (Exception e)
				{
					//NOTE(Simon): If upload fails, just delete everything so we can start fresh next time.
					//TODO(Simon): Look into supporting partial uploads
					Directory.Delete(basePath, true);
					await WriteError(context, "Something went wrong while uploading this file", StatusCodes.Status500InternalServerError, e);
				}
			}
			else
			{
				await WriteError(context, "{}", StatusCodes.Status401Unauthorized);
			}
		}

		[Route("GET", "/api/meta")]
		[Route("GET", "/api/v1/meta")]
		private static async Task MetaGet(HttpContext context)
		{
			var args = context.Request.Query;
			string id = args["videoid"].ToString();

			if (Guid.TryParse(id, out _))
			{
				string filename = Path.Combine(baseFilePath, id, "meta.json");
				await WriteFile(context, filename, "application/json", "meta.json");
			}
			else
			{
				await Write404(context);
			}
		}

		[Route("GET", "/api/thumbnail")]
		[Route("GET", "/api/v1/thumbnail")]
		private static async Task ThumbnailGet(HttpContext context)
		{
			var args = context.Request.Query;
			string id = args["id"];

			if (Guid.TryParse(id, out _))
			{
				string filename = Path.Combine(baseFilePath, id, "thumb.jpg");
				await WriteFile(context, filename, "image/jpg", "thumb.jpg");
			}
			else
			{
				await Write404(context);
			}
		}

		[Route("GET", "/api/extra")]
		[Route("GET", "/api/v1/extra")]
		private static async Task ExtraGet(HttpContext context)
		{
			var args = context.Request.Query;
			string videoId = args["videoid"];
			string extraId = args["extraid"];

			if (String.IsNullOrEmpty(videoId) || String.IsNullOrEmpty(extraId))
			{
				await Write404(context);
				return;
			}

			if (Guid.TryParse(videoId, out _) && Guid.TryParse(extraId, out _))
			{
				string filename = Path.Combine(baseFilePath, videoId, "extra", extraId);
				await WriteFile(context, filename, "application/octet-stream", "");
			}
			else
			{
				await Write404(context);
			}
		}

		[Route("GET", "/api/extras")]
		[Route("GET", "/api/v1/extras")]
		private static async Task ExtrasGet(HttpContext context)
		{
			var args = context.Request.Query;
			string idstring = args["videoid"];

			if (String.IsNullOrEmpty(idstring))
			{
				await Write404(context);
				return;
			}

			using var connection = Database.OpenNewConnection();

			if (Guid.TryParse(idstring, out var videoId))
			{
				try
				{
					var ids = await connection.QueryAsync<Guid>(@"select guid from extra_files where video_id = @videoId", new { videoId });
					var stringIds = new List<string>();
					foreach (var id in ids) { stringIds.Add(id.ToString().Replace("-", "")); }
					await context.Response.Body.WriteAsync(Utf8Json.JsonSerializer.SerializeUnsafe(stringIds));
				}
				catch (Exception e)
				{
					await WriteError(context, "Something went wrong while processing this request", StatusCodes.Status500InternalServerError, e);
				}
			}
		}

		[Route("POST", "/api/extras")]
		[Route("POST", "/api/v1/extras")]
		private static async Task ExtrasPost(HttpContext context)
		{
			using var connection = Database.OpenNewConnection();

			var form = context.Request.Form;
			string token = form["token"];
			string videoGuid = form["videoguid"];
			string rawExtraGuids = form["extraguids"];
			var extraGuids = rawExtraGuids.Split(',');

			if (await UserController.AuthenticateToken(token, connection))
			{
				string basePath = Path.Combine(baseFilePath, videoGuid);
				string extraPath = Path.Combine(basePath, "extra");
				try
				{
					Directory.CreateDirectory(extraPath);

					foreach (var file in form.Files)
					{
						using (var stream = new FileStream(Path.Combine(extraPath, file.Name), FileMode.OpenOrCreate))
						{
							await file.CopyToAsync(stream);
						}
					}

					var clearTask = connection.ExecuteAsync("delete from extra_files where video_id = @videoGuid::uuid", new { videoGuid });

					var param = new[]
					{
						new { video_id = "", guid = "" }
					}.ToList();

					param.Clear();

					foreach (var id in extraGuids)
					{
						param.Add(new { video_id = videoGuid, guid = id });
					}

					var downloadSizeTask = Task.Run(() => GetDirectorySize(new DirectoryInfo(basePath)));

					await clearTask;
					await connection.ExecuteAsync("insert into extra_files (video_id, guid) values (@video_id::uuid, @guid::uuid)", param);
					long downloadSize = await downloadSizeTask;
					await connection.ExecuteAsync("update videos set (downloadsize) = (@downloadSize) where id = @videoGuid::uuid", new { videoGuid, downloadsize = downloadSize });

					await context.Response.WriteAsync("{}");
				}
				catch (Exception e)
				{
					//NOTE(Simon): If upload fails, just delete everything so we can start fresh next time.
					//TODO(Simon): Look into supporting partial uploads
					Directory.Delete(basePath, true);
					await WriteError(context, "Something went wrong while uploading this file", StatusCodes.Status500InternalServerError, e);
				}
			}
			else
			{
				await WriteError(context, "{}", StatusCodes.Status401Unauthorized);
			}
		}

		[Route("GET", "/")]
		private static async Task IndexGet(HttpContext context)
		{
			SetHTMLContentType(context);

			int count = 10;
			int offset = 0;

			using var connection = Database.OpenNewConnection();

			var videos = await connection.QueryAsync<Video>(@"select v.id, v.userid, u.username, v.timestamp, v.downloadsize, v.title, v.description, v.length from videos v
								inner join users u on v.userid = u.userid
								order by v.timestamp desc
								limit @count
								offset @offset", new { count, offset });

			var templateContext = new TemplateContext(new {videos});

			await context.Response.WriteAsync(await HTMLRenderer.Render("Templates\\index.liquid", templateContext));
		}


		private static bool MatchPath(PathString fullPath, PathString startSegment)
		{
			return fullPath.StartsWithSegments(startSegment, StringComparison.OrdinalIgnoreCase);
		}

		private static async Task<bool> UserOwnsVideo(Guid guid, int userId, NpgsqlConnection connection)
		{
			int count;
			try
			{
				count = await connection.QuerySingleAsync<int>("select count(*) from videos where id=@guid and userid=@userid", new { guid, userId });
			}
			catch
			{
				return false;
			}

			return count > 0;
		}

		private static async Task<bool> VideoExists(Guid guid, NpgsqlConnection connection)
		{
			int count;
			try
			{
				count = await connection.QuerySingleAsync<int>("select count(*) from videos where id=$1", new { guid });
			}
			catch
			{
				return false;
			}

			return count > 0;
		}

		private static Meta ReadMetaFile(string path)
		{
			var raw = File.ReadAllText(path).AsSpan();
			var meta = new Meta();

			try
			{
				_ = GetNextMetaValue(ref raw);
				meta.guid = Guid.Parse(GetNextMetaValue(ref raw));
				meta.title = GetNextMetaValue(ref raw).ToString();
				meta.description = GetNextMetaValue(ref raw).ToString();
				meta.length = (int)float.Parse(GetNextMetaValue(ref raw));

				return meta;
			}
			catch
			{
				return null;
			}
		}

		private static ReadOnlySpan<char> GetNextMetaValue(ref ReadOnlySpan<char> text)
		{
			int start = text.IndexOf(':') + 1;
			int end = text.IndexOf('\n');
			int length = end - start - 1;

			var line = text.Slice(start, length);
			text = text.Slice(end + 1);

			return line;
		}

		private static long GetDirectorySize(DirectoryInfo d)
		{
			long size = 0;

			var files = d.GetFiles("*.*", SearchOption.AllDirectories);
			foreach (var file in files)
			{
				size += file.Length;
			}

			var subDirs = d.GetDirectories();
			foreach (var dir in subDirs)
			{
				size += GetDirectorySize(dir);
			}
			return size;
		}

	}
}
