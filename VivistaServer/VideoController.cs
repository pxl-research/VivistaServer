using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Caching;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dapper;
using Fluid;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Npgsql;
using tusdotnet.Interfaces;
using tusdotnet.Models.Configuration;

namespace VivistaServer
{
	public class VideoController
	{
		private const int indexCountDefault = 10;
		public static readonly string baseFilePath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? @"C:\VivistaData\" : "/srv/vivistadata/";

		private static MemoryCache uploadAuthorisationCache = new MemoryCache(UPLOAD_AUTHORISATION_CACHE_NAME);
		private static MemoryCache viewHistoryCache = new MemoryCache(VIEWHISTORY_CACHE_NAME);
		private const string VIEWHISTORY_CACHE_NAME = "viewHistoryCache";
		private const string UPLOAD_AUTHORISATION_CACHE_NAME = "viewHistoryCache";

		public class Video
		{
			public Guid id;
			public int userid;
			public string username;
			public DateTime timestamp;
			public long downloadsize;
			public int views;
			public int downloads;
			public VideoPrivacy privacy;

			public string title;
			public string description;
			public int length;

			public bool isPublic => privacy == VideoPrivacy.Public;
			public bool isPrivate => privacy == VideoPrivacy.Private;
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

		public enum UploadFileType
		{
			Video,
			Meta,
			Tags,
			Chapters,
			Extra,
			Miniature
		}

		private enum IndexTab
		{
			New,
			Popular,
			MostWatched
		}

		//NOTE(Simon): DO NOT remove or reorder items. Adding is allowed
		public enum VideoPrivacy
		{
			Public,
			Organization,
			Unlisted,
			Private,
			Processing
		}


		[Route("GET", "/api/videos")]
		[Route("GET", "/api/v1/videos")]
		private static async Task VideosGetApi(HttpContext context)
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
				userid = await UserController.UserIdFromUsername(author, connection);
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
								and (@uploadDate::timestamp is NULL or v.timestamp>=@uploadDate)", new {userid, uploadDate});
				videos.videos = await GetIndexVideos(IndexTab.MostWatched, count, offset, connection);

				videos.count = videos.videos.AsList().Count;
				videos.page = videos.totalcount > 0 ? offset / videos.totalcount + 1 : 1;
				await context.Response.Body.WriteAsync(Utf8Json.JsonSerializer.SerializeUnsafe(videos));
			}
			catch (Exception e)
			{
				await CommonController.WriteError(context, "Something went wrong while processing this request", StatusCodes.Status500InternalServerError, e);
			}
		}

		[Route("GET", "/api/video")]
		[Route("GET", "/api/v1/video")]
		private static async Task VideoGetApi(HttpContext context)
		{
			var query = context.Request.Query;

			if (!Guid.TryParse(query["id"], out var videoid))
			{
				await CommonController.Write404(context);
				return;
			}

			using var connection = Database.OpenNewConnection();

			Video video;
			try
			{
				video = await GetVideo(videoid, connection);

				if (video == null || video.isPrivate)
				{
					await CommonController.Write404(context);
					return;
				}
			}
			catch (Exception e)
			{
				await CommonController.WriteError(context, "Something went wrong while processing this request", StatusCodes.Status500InternalServerError, e);
				return;
			}

			await context.Response.Body.WriteAsync(Utf8Json.JsonSerializer.SerializeUnsafe(video));
		}

		[Route("GET", "/api/thumbnail")]
		[Route("GET", "/api/v1/thumbnail")]
		private static async Task ThumbnailGetApi(HttpContext context)
		{
			var args = context.Request.Query;
			string id = args["id"];

			if (Guid.TryParse(id, out _))
			{
				string filename = Path.Combine(baseFilePath, id, "thumb.jpg");

				context.Response.Headers.Add(HeaderNames.CacheControl, $"max-age={24 * 60 * 60}");
				await CommonController.WriteFile(context, filename, "image/jpg", "thumb.jpg");
			}
			else
			{
				await CommonController.Write404(context);
			}
		}

		[Route("GET", "/api/file")]
		[Route("GET", "/api/v1/file")]
		private static async Task FileGetApi(HttpContext context)
		{
			var args = context.Request.Query;
			string videoId = args["videoid"];
			string filename = args["filename"];

			if (String.IsNullOrEmpty(videoId) || String.IsNullOrEmpty(filename))
			{
				await CommonController.Write404(context);
				return;
			}

			if (Guid.TryParse(videoId, out var guid))
			{
				using var connection = Database.OpenNewConnection();

				var video = await GetVideo(guid, connection);

				if (video.isPublic)
				{
					string path = Path.Combine(baseFilePath, videoId, filename);
					await CommonController.WriteFile(context, path, "application/octet-stream", filename);
				}
				else
				{
					await CommonController.Write404(context);
				}
			}
			else
			{
				await CommonController.Write404(context);
			}
		}

		[Route("GET", "/api/files")]
		[Route("GET", "/api/v1/files")]
		private static async Task FilesGetApi(HttpContext context)
		{
			var args = context.Request.Query;
			string videoid = args["videoid"];

			if (String.IsNullOrEmpty(videoid))
			{
				await CommonController.Write404(context);
				return;
			}

			if (Guid.TryParse(videoid, out var guid))
			{
				using var connection = Database.OpenNewConnection();

				var video = await GetVideo(guid, connection);

				if (video.isPublic)
				{
					try
					{
						string path = Path.Combine(baseFilePath, guid.ToString());
						var di = new DirectoryInfo(path);
						var files = di.GetFiles("", SearchOption.AllDirectories);
						var filenames = files.Select(x => x.FullName.Substring(path.Length + 1));
						await context.Response.Body.WriteAsync(Utf8Json.JsonSerializer.SerializeUnsafe(new { array = filenames }));
						await AddVideoDownload(guid, connection);
					}
					catch (Exception e)
					{
						await CommonController.WriteError(context, "Something went wrong while processing this request", StatusCodes.Status500InternalServerError, e);
					}
				}
				else
				{
					await CommonController.Write404(context);
				}
			}
			else
			{
				await CommonController.Write404(context);
			}
		}

		[Route("POST", "/api/finish_upload")]
		[Route("POST", "/api/v1/finish_upload")]
		private static async Task FinishUploadApi(HttpContext context)
		{
			var form = context.Request.Form;
			if (Guid.TryParse(form["id"], out var guid))
			{
				var userTask = UserSessions.GetLoggedInUser(context);

				using var connection = Database.OpenNewConnection();

				var user = await userTask;
				var video = await GetVideo(guid, connection);

				if (user != null && await UserOwnsVideo(guid, user.userid, connection))
				{
					var directoryPath = Path.Combine(baseFilePath, guid.ToString());
					var videoPath = Path.Combine(directoryPath, "main.mp4");
					var thumbPath = Path.Combine(directoryPath, "thumb.jpg");

					var process = new Process();
					process.StartInfo.UseShellExecute = false;
					process.StartInfo.RedirectStandardOutput = true;
					process.StartInfo.RedirectStandardError = true;

					string ffmpegArgs = $"-hide_banner -loglevel error -y -ss 00:00:05 -i {videoPath} -frames:v 1 -q:v 3 -s 720x360 {thumbPath}";

					if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
					{
						//process.StartInfo.FileName = @"\bin\ffmpeg.exe";
						process.StartInfo.FileName = "/usr/bin/ffmpeg";
						process.StartInfo.Arguments = $"{ffmpegArgs}";
						//process.StartInfo.Arguments = $"-c ffmpeg {ffmpegArgs}";
						Console.WriteLine($"Running following command: {process.StartInfo.Arguments}");
					}
					else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
					{
						process.StartInfo.FileName = "cmd.exe";
						process.StartInfo.Arguments = $"/C ffmpeg.exe {ffmpegArgs}";
						Console.WriteLine($"Running following command: {process.StartInfo.Arguments}");
					}

					process.Start();

					await process.WaitForExitAsync();
					Console.WriteLine($"Ffmpeg exit code: {process.ExitCode}");
					if (process.ExitCode == 0)
					{
						Console.WriteLine("Thumbnail generated succesfully");
						video.privacy = VideoPrivacy.Public;
						video.downloadsize = (int)GetDirectorySize(new DirectoryInfo(directoryPath));
						video.length = await FfmpegGetVideoLength(videoPath);

						var meta = ReadMetaFile(Path.Combine(directoryPath, "meta.json"));

						video.title = meta.title;
						video.description = meta.description;
						
						Console.WriteLine($"Video Privacy: {video.privacy}");
						Console.WriteLine($"Video Size: {video.downloadsize}");
						Console.WriteLine($"Video length: {video.length}");

						if (await AddOrUpdateVideo(video, connection))
						{
							Console.WriteLine("Database row updated succesfully");
							await context.Response.WriteAsJsonAsync(new { success = true});
						}
						else
						{
							Console.WriteLine("Error while updating database row for video");
							await CommonController.WriteError(context, "{}", StatusCodes.Status500InternalServerError);
						}
					}
					else
					{
						Console.WriteLine("Error while generating thumbnail");
						Console.WriteLine($"\t {process.StandardOutput.ReadToEnd()}");
						Console.WriteLine($"\t {process.StandardError.ReadToEnd()}");
						await CommonController.WriteError(context, "{}", StatusCodes.Status500InternalServerError);
					}
				}
				else
				{ 
					Console.WriteLine("User does not own video");
					await CommonController.WriteError(context, "{}", StatusCodes.Status401Unauthorized);
				}
			}
			else
			{
				await CommonController.WriteError(context, "{}", StatusCodes.Status400BadRequest);
			}
		}



		[Route("GET", "/")]
		private static async Task IndexGet(HttpContext context)
		{
			CommonController.SetHTMLContentType(context);
			var tabString = context.Request.Query["tab"].ToString();
			var tab = tabString switch
			{
				"new" => IndexTab.New,
				"popular" => IndexTab.Popular,
				_ => IndexTab.MostWatched
			};

			int count = 20;
			int offset = 0;

			using var connection = Database.OpenNewConnection();

			var videos = await GetIndexVideos(tab, count, offset, connection);

			var templateContext = new TemplateContext(new { videos, tab = tab.ToString() });

			await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\index.liquid", templateContext));
		}

		//TODO(Simon): Related videos, tags
		[Route("GET", "/video")]
		private static async Task VideoGet(HttpContext context)
		{
			CommonController.SetHTMLContentType(context);

			if (GuidHelpers.TryDecode(context.Request.Query["id"], out var videoId))
			{
				using var connection = Database.OpenNewConnection();
				var video = await GetVideo(videoId, connection);

				if (video != null && !video.isPrivate)
				{
					var relatedVideos = new List<Video>();

					var templateContext = new TemplateContext(new { video, relatedVideos});
					await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\video.liquid", templateContext));
					await AddVideoView(video.id, context, connection);
				}
				else
				{
					await CommonController.Write404(context);
				}
			}
			else
			{
				await CommonController.Write404(context);
			}
		}

		[Route("GET", "/my_videos")]
		private static async Task MyVideosGet(HttpContext context)
		{
			CommonController.SetHTMLContentType(context);

			var userTask = UserSessions.GetLoggedInUser(context);
			using var connection = Database.OpenNewConnection();
			var user = await userTask;

			if (user != null)
			{
				const int countPerPage = 20;
				int page = 1;
				if (context.Request.Query.ContainsKey("page"))
				{
					Int32.TryParse(context.Request.Query["page"], out page);
				}

				int offset = (page - 1) * countPerPage;

				var numVideos = await NumVideosForUser(user.userid, connection);
				var VideosTask = VideosForUser(user.userid, countPerPage, offset, connection);

				var pagination = new Pagination(numVideos, countPerPage, offset);

				var templateContext = new TemplateContext(new { videos = await VideosTask, pagination });

				await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\myVideos.liquid", templateContext));
			}
			else
			{
				await CommonController.Write404(context);
			}
		}

		[Route("GET", "/user")]
		private static async Task UserGet(HttpContext context)
		{
			CommonController.SetHTMLContentType(context);

			var username = context.Request.Query["name"].ToString();

			if (!String.IsNullOrEmpty(username))
			{
				using var connection = Database.OpenNewConnection();
				var user = await UserController.UserFromUsername(username, connection);

				if (user != null)
				{
					const int countPerPage = 20;
					int page = 1;
					if (context.Request.Query.ContainsKey("page"))
					{
						Int32.TryParse(context.Request.Query["page"], out page);
					}

					int offset = (page - 1) * countPerPage;

					var numVideos = await NumPublicVideosForUser(user.userid, connection);
					var VideosTask = PublicVideosForUser(user.userid, countPerPage, offset, connection);

					var pagination = new Pagination(numVideos, countPerPage, offset);

					var templateContext = new TemplateContext(new {videos = await VideosTask, user, pagination});

					await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\user.liquid", templateContext));
				}
				else
				{
					await CommonController.Write404(context);
				}
			}
			else
			{
				await CommonController.Write404(context);
			}
		}

		[Route("GET", "/delete_video")]
		private static async Task DeleteVideoGet(HttpContext context)
		{
			CommonController.SetHTMLContentType(context);

			var userTask = UserSessions.GetLoggedInUser(context);
			using var connection = Database.OpenNewConnection();
			var user = await userTask;

			if (user != null && GuidHelpers.TryDecode(context.Request.Query["id"], out var videoId))
			{
				var video = await GetVideo(videoId, connection);
				if (UserOwnsVideo(video, user.userid))
				{
					var templateContext = new TemplateContext(new {video});
					await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\deleteVideoConfirm.liquid", templateContext));
				}
				else
				{
					await CommonController.Write404(context);
				}
			}
			else
			{
				await CommonController.Write404(context);
			}

		}

		[Route("GET", "/delete_video_confirm")]
		private static async Task DeleteVideoConfirmGet(HttpContext context)
		{
			CommonController.SetHTMLContentType(context);

			var userTask = UserSessions.GetLoggedInUser(context);
			using var connection = Database.OpenNewConnection();
			var user = await userTask;

			if (user != null && GuidHelpers.TryDecode(context.Request.Query["id"], out var videoId))
			{
				var video = await GetVideo(videoId, connection);
				if (UserOwnsVideo(video, user.userid))
				{
					await DeleteVideo(video.id, connection);

					context.Response.Redirect("/my_videos");
				}
				else
				{
					await CommonController.Write404(context);
				}
			}
			else
			{
				await CommonController.Write404(context);
			}
		}

		[Route("POST", "/update_video_privacy")]
		private static async Task UpdateVideoPrivacyPost(HttpContext context)
		{
			CommonController.SetHTMLContentType(context);

			var userTask = UserSessions.GetLoggedInUser(context);
			using var connection = Database.OpenNewConnection();
			var user = await userTask;

			if (user != null && GuidHelpers.TryDecode(context.Request.Query["id"], out var videoid))
			{
				var video = await GetVideo(videoid, connection);
				if (UserOwnsVideo(video, user.userid))
				{
					if (Int32.TryParse(context.Request.Form["video-privacy"], out int privacy))
					{
						await SetVideoPrivacy(video.id, (VideoPrivacy)privacy, connection);
					}
				}
			}

			context.Response.Redirect("/my_videos");
		}

		[Route("GET", "/search")]
		private static async Task SearchPost(HttpContext context)
		{
			CommonController.SetHTMLContentType(context);

			var searchQuery = context.Request.Query["q"].ToString();

			if (!String.IsNullOrEmpty(searchQuery))
			{
				using var connection = Database.OpenNewConnection();

				var normalizedQuery = searchQuery.NormalizeForSearch();

				var channels = await FindUsersFuzzy(normalizedQuery, 3, connection);
				var videos  = await FindVideosFuzzy(normalizedQuery, 20, connection);

				bool hasResults = channels.Any() || videos.Any();

				var templateContext = new TemplateContext(new { channels, videos, searchQuery, hasResults });
				await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\search.liquid", templateContext));
			}
			else
			{
				await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\search.liquid", null));
			}
		}

		public static async Task AuthorizeUploadTus(AuthorizeContext arg)
		{
			Console.WriteLine("Authorizing upload request");
			var user = await UserSessions.GetLoggedInUser(arg.HttpContext);
			if (user != null)
			{
				var video = new Video
				{
					id = new Guid(arg.HttpContext.Request.Headers["guid"]),
					userid = user.userid,
					privacy = VideoPrivacy.Processing,
				};

				Console.WriteLine("Video metadata:");
				Console.WriteLine($"\t id: {video.id}");
				Console.WriteLine($"\t userid: {video.userid}");
				Console.WriteLine($"\t privacy: {video.privacy}");


				var cachedOwner = (User)uploadAuthorisationCache[video.id.ToString()];
				bool exists;
				bool owns;

				if (cachedOwner == null)
				{
					Console.WriteLine("No owner found in cache");
					using var connection = Database.OpenNewConnection();
					exists = await VideoExists(video.id, connection);
					owns = await UserOwnsVideo(video.id, user.userid, connection);
					await AddOrUpdateVideo(video, connection);
				}
				//NOTE(Simon): At this point the video has definitely been created, so it exists and is owned by the cached user
				else if (cachedOwner.userid == user.userid)
				{
					Console.WriteLine("User is owner of video. allow upload");
					exists = true;
					owns = true;
				}
				else
				{
					Console.WriteLine("User not authorized to update this video");
					arg.FailRequest("This user is not authorized to update this video");
					return;
				}

				if (exists && owns || !exists)
				{
					Console.WriteLine("Adding user to cache");
					uploadAuthorisationCache.Add(video.id.ToString(), user, new CacheItemPolicy { SlidingExpiration = TimeSpan.FromMinutes(10) });
					return;
				}
			}

			arg.FailRequest("This user is not authorized to update this video");
			Console.WriteLine("User not authorized to upload videos");
		}

		public static async Task ProcessUploadTus(FileCompleteContext arg)
		{
			Console.WriteLine("Begin processing upload");
			var context = arg.HttpContext;
			var headers = context.Request.Headers;

			if (Enum.TryParse<UploadFileType>(headers["type"].ToString(), out var type))
			{
				if (Guid.TryParse(headers["guid"], out var guid))
				{
					var newFilename = headers["filename"];

					//NOTE(Simon): Check if provided filename is a guid
					if (!String.IsNullOrEmpty(newFilename))
					{
						Console.WriteLine("Upload has correct headers");
						var path = Path.Combine(baseFilePath, guid.ToString());
						var tusFilePath = Path.Combine(path, arg.FileId);
						string newFilePath;

						switch (type)
						{
							case UploadFileType.Video:
							case UploadFileType.Meta:
							case UploadFileType.Tags:
							case UploadFileType.Chapters:
								newFilePath = Path.Combine(path, newFilename);
								break;
							case UploadFileType.Extra:
								newFilePath = Path.Combine(path, "extra", newFilename);
								break;
							case UploadFileType.Miniature:
								newFilePath = Path.Combine(path, "areaMiniatures", newFilename);
								break;
							default:
								await CommonController.WriteError(context, "Unknown file type", StatusCodes.Status400BadRequest);
								return;
						}

						Console.WriteLine($"Creating directory for {newFilePath} and moving uploaded file there");
						Directory.CreateDirectory(Path.GetDirectoryName(newFilePath));
						File.Move(tusFilePath, newFilePath, true);
						await ((ITusTerminationStore)arg.Store).DeleteFileAsync(arg.FileId, arg.CancellationToken);
					}
					else
					{
						await CommonController.WriteError(arg.HttpContext, "The project being uploaded is corrupted", StatusCodes.Status400BadRequest);
					}
				}
			}
		}



		public static async Task<IEnumerable<Video>> VideosForUser(int userid, int count, int offset, NpgsqlConnection connection)
		{
			try
			{
				var videos = await connection.QueryAsync<Video>(@"select * from videos
																where userid=@userid
																order by timestamp desc
																limit @count
																offset @offset", new { userid, count, offset });

				return videos;
			}
			catch (Exception e)
			{
				return new List<Video>();
			}
		}

		public static async Task<IEnumerable<Video>> PublicVideosForUser(int userid, int count, int offset, NpgsqlConnection connection)
		{
			try
			{
				var videos = await connection.QueryAsync<Video>(@"select * from videos
																where userid=@userid and privacy=@privacy
																order by timestamp desc
																limit @count
																offset @offset", new { userid, count, offset, privacy = VideoPrivacy.Public });

				return videos;
			}
			catch (Exception e)
			{
				return new List<Video>();
			}
		}

		private static async Task<int> NumVideosForUser(int userid, NpgsqlConnection connection)
		{
			try
			{
				int totalcount = await connection.QuerySingleAsync<int>(@"select count(*) from videos
																		where userid=@userid", new { userid });
				return totalcount;
			}
			catch (Exception e)
			{
				return 0;
			}
		}

		public static async Task<int> NumPublicVideosForUser(int userid, NpgsqlConnection connection)
		{
			try
			{
				int totalcount = await connection.QuerySingleAsync<int>(@"select count(*) from videos
																		where userid=@userid and privacy=@privacy",
																		new { userid, privacy = VideoPrivacy.Public });
				return totalcount;
			}
			catch (Exception e)
			{
				return 0;
			}
		}

		private static async Task<bool> UserOwnsVideo(Guid guid, int userId, NpgsqlConnection connection)
		{
			int count;
			try
			{
				count = await connection.QuerySingleAsync<int>(@"select count(*) from videos where id=@guid and userid=@userId", new { guid, userId });
			}
			catch
			{
				return false;
			}

			return count > 0;
		}

		private static bool UserOwnsVideo(Video video, int userId)
		{
			return video?.userid == userId;
		}

		private static async Task<bool> VideoExists(Guid guid, NpgsqlConnection connection)
		{
			int count;
			try
			{
				count = await connection.QuerySingleAsync<int>(@"select count(*) from videos where id=@guid", new { guid });
			}
			catch
			{
				return false;
			}

			return count > 0;
		}

		private static async Task<Video> GetVideo(Guid videoid, NpgsqlConnection connection)
		{
			try
			{
				var video = await connection.QuerySingleAsync<Video>(@"select v.*, u.username from videos v
													inner join users u on v.userid = u.userid
													where v.id=@videoid::uuid", new {videoid});

				return video;
			}
			catch (Exception e)
			{
				return null;
			}
		}

		private static async Task<IEnumerable<Video>> GetIndexVideos(IndexTab tab, int count, int offset, NpgsqlConnection connection)
		{
			if (tab == IndexTab.New)
			{
				return await connection.QueryAsync<Video>(@"select v.*, u.username from videos v
								inner join users u on v.userid = u.userid
								where privacy=@privacy
								order by v.timestamp desc
								limit @count
								offset @offset", new { count, offset, privacy = VideoPrivacy.Public });
			}

			if (tab == IndexTab.MostWatched)
			{
				return await connection.QueryAsync<Video>(@"select v.*, u.username from videos v
								inner join users u on v.userid = u.userid
								where privacy=@privacy
								order by v.views desc
								limit @count
								offset @offset", new { count, offset, privacy = VideoPrivacy.Public });
			}

			if (tab == IndexTab.Popular)
			{
				return await connection.QueryAsync<Video>(@"select v.*, u.username from videos v
								inner join users u on v.userid = u.userid
								where privacy=@privacy
								order by v.views desc
								limit @count
								offset @offset", new { count, offset, privacy = VideoPrivacy.Public });
			}

			return null;
		}

		private static async Task<bool> AddOrUpdateVideo(Video video, NpgsqlConnection connection)
		{
			try
			{
				var timestamp = DateTime.UtcNow;
				await connection.ExecuteAsync(@"INSERT INTO videos (id, userid, title, description, length, downloadsize, timestamp, privacy)
												VALUES (@id::uuid, @userid, @title, @description, @length, @downloadsize, @timestamp, @privacy)
												ON CONFLICT(id) DO UPDATE
												SET title=@title, description=@description, length=@length, downloadsize=@downloadsize",
												new {video.id, video.userid, video.title, video.description, video.length, video.downloadsize, timestamp, video.privacy});

				return true;
			}
			catch (Exception e)
			{
				return false;
			}
		}

		private static async Task<bool> SetVideoPrivacy(Guid videoid, VideoPrivacy privacy, NpgsqlConnection connection)
		{
			try
			{
				var success = await connection.ExecuteAsync(@"update videos
															set privacy=@privacy
															where id=@videoid::uuid", new { videoid, privacy = (int)privacy });

				return success > 0;
			}
			catch (Exception e)
			{
				return false;
			}
		}

		private static async Task<bool> AddVideoView(Guid videoid, HttpContext context, NpgsqlConnection connection)
		{
			var ip = context.Connection.RemoteIpAddress;

			bool inCache = viewHistoryCache.Get(ip.ToString()) != null;

			if (!inCache)
			{
				try
				{
					var success = await connection.ExecuteAsync(@"update videos
															set views = views + 1
															where id=@videoid::uuid", new {videoid});

					viewHistoryCache.Add(ip.ToString(), new object(), DateTimeOffset.Now.AddMinutes(5));

					return success > 0;
				}
				catch (Exception e)
				{
					return false;
				}
			}

			return true;
		}

		private static async Task<bool> AddVideoDownload(Guid videoid, NpgsqlConnection connection)
		{
			try
			{
				var success = await connection.ExecuteAsync(@"update videos
															set downloads = downloads + 1
															where id=@videoid:uuid", new { videoid });

				return success > 0;
			}
			catch (Exception e)
			{
				return false;
			}
		}

		private static async Task<bool> DeleteVideo(Guid videoid, NpgsqlConnection connection)
		{
			try
			{
				var result = await connection.ExecuteAsync(@"delete from videos
															where id=@videoid::uuid", new { videoid });
				return result > 0;
			}
			catch (Exception e)
			{
				return false;
			}
		}

		//NOTE(Simon): Perform a fuzzy search for user, based on a trigram index on usernames
		private static async Task<IEnumerable<User>> FindUsersFuzzy(string query, int count, NpgsqlConnection connection)
		{
			try
			{
				var result = await connection.QueryAsync<User>(@"SELECT *
															  from users
															  where username % @query
															  order by similarity(username, @query) desc, username
															  limit @count", new { query, count });

				return result;
			}
			catch (Exception e)
			{
				return new List<User>();
			}
		}

		//NOTE(Simon): Perform a fuzzy search for videos, based on a trigram index
		private static async Task<IEnumerable<Video>> FindVideosFuzzy(string query, int count, NpgsqlConnection connection)
		{
			try
			{
				var result = await connection.QueryAsync<Video>(@"select ts_rank(search, websearch_to_tsquery('english', @query)) as rank, v.*, u.username
																from videos v
																inner join users u on v.userid = u.userid
																where search @@ to_tsquery('english', @query)
																order by rank
																limit @count", new {query, count});
				return result;
			}
			catch (Exception e)
			{
				return new List<Video>();
			}
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

		private static async Task<int> FfmpegGetVideoLength(string videoPath)
		{
			Console.WriteLine($"Getting video length with ffprobe");
			var process = new Process();
			process.StartInfo.UseShellExecute = false;
			process.StartInfo.RedirectStandardOutput = true;
			process.StartInfo.RedirectStandardError = true;

			string ffmpegArgs = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 {videoPath}";

			if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
			{
				process.StartInfo.FileName = "/usr/bin/ffprobe";
				process.StartInfo.Arguments = $"{ffmpegArgs}";
			}
			else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				process.StartInfo.FileName = "cmd.exe";
				process.StartInfo.Arguments = $"/C ffprobe.exe {ffmpegArgs}";
			}

			process.Start();

			await process.WaitForExitAsync();
			Console.WriteLine($"ffprobe exit code {process.ExitCode}");
			if (process.ExitCode == 0)
			{
				Console.WriteLine($"ffprobe run succesful");
				return (int)float.Parse(process.StandardOutput.ReadLine());
			}

			return 0;
		}
	}
}
