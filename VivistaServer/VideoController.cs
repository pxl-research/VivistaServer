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
using Microsoft.AspNetCore.Mvc;
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
			public string userPicture;
			public DateTime timestamp;
			public long downloadsize;
			public int views;
			public int downloads;
			public VideoPrivacy privacy;
			public int privacyInt => (int)privacy;

			public string title;
			public string description;
			public int length;
			public List<string> tags;

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

		public class Playlist
		{
			public Guid id { get; set; }
			public string name { get; set; }
			public int userid { get; set; }
			public string username { get; set; }
			public VideoPrivacy privacy { get; set; }
			public int count { get; set; }
			public bool playListContainsVideo { get; set; }
			public List<Video> videos { get; set; }
			public string idBase64 { get; set; }
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
				userid = await UserController.UserIdFromUsername(author, connection, context);
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
				videos.totalcount = await Database.QuerySingleAsync<int>(connection, @"select count(*) from videos v
								inner join users u on v.userid = u.userid
								where (@userid::int is NULL or v.userid=@userid)
								and (@uploadDate::timestamp is NULL or v.timestamp>=@uploadDate)", context, new { userid, uploadDate });
				videos.videos = await GetIndexVideos(IndexTab.MostWatched, count, offset, connection, context);

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
				video = await GetVideo(videoid, connection, context);

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

				var video = await GetVideo(guid, connection, context);

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

				var video = await GetVideo(guid, connection, context);

				if (UserCanViewVideo(video, null))
				{
					try
					{
						string path = Path.Combine(baseFilePath, guid.ToString());
						var di = new DirectoryInfo(path);
						var files = di.GetFiles("", SearchOption.AllDirectories);
						var filenames = files.Select(x => x.FullName.Substring(path.Length + 1));
						await context.Response.Body.WriteAsync(Utf8Json.JsonSerializer.SerializeUnsafe(new { array = filenames }));
						await AddVideoDownload(guid, connection, context);
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
				var video = await GetVideo(guid, connection, context);

				if (user != null && await UserOwnsVideo(guid, user.userid, connection, context))
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
						CommonController.LogDebug($"Running following command: {process.StartInfo.Arguments}");
					}
					else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
					{
						process.StartInfo.FileName = "cmd.exe";
						process.StartInfo.Arguments = $"/C ffmpeg.exe {ffmpegArgs}";
						CommonController.LogDebug($"Running following command: {process.StartInfo.Arguments}");
					}

					process.Start();

					await process.WaitForExitAsync();
					CommonController.LogDebug($"Ffmpeg exit code: {process.ExitCode}");
					if (process.ExitCode == 0)
					{
						CommonController.LogDebug("Thumbnail generated succesfully");
						video.privacy = VideoPrivacy.Unlisted;
						video.downloadsize = (int)GetDirectorySize(new DirectoryInfo(directoryPath));
						video.length = await FfmpegGetVideoLength(videoPath);

						var meta = ReadMetaFile(Path.Combine(directoryPath, "meta.json"));

						video.title = meta.title;
						video.description = meta.description;

						CommonController.LogDebug($"Video Privacy: {video.privacy}");
						CommonController.LogDebug($"Video Size: {video.downloadsize}");
						CommonController.LogDebug($"Video length: {video.length}");

						if (await AddOrUpdateVideo(video, connection, context))
						{
							CommonController.LogDebug("Database row updated succesfully");
							await context.Response.WriteAsJsonAsync(new { success = true });

							CleanPartialUploads(video.id);
							DashboardController.AddUpload();
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
					CommonController.LogDebug("User does not own video");
					await CommonController.WriteError(context, "{}", StatusCodes.Status401Unauthorized);
				}
			}
			else
			{
				await CommonController.WriteError(context, "{}", StatusCodes.Status400BadRequest);
			}
		}

		private static void CleanPartialUploads(Guid videoId)
		{
			string directory = Path.Combine(baseFilePath, videoId.ToString());
			var files = new List<string>();
			files.AddRange(Directory.GetFiles(directory, "*.chunkstart"));
			files.AddRange(Directory.GetFiles(directory, "*.chunkcomplete"));
			files.AddRange(Directory.GetFiles(directory, "*.metadata"));
			files.AddRange(Directory.GetFiles(directory, "*.uploadlength"));

			var filesToFilter = Directory.GetFiles(directory);
			foreach (var file in filesToFilter)
			{
				if (Path.GetExtension(file) == String.Empty)
				{
					files.Add(file);
				}
			}

			foreach (var file in files)
			{
				File.Delete(file);
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

			var videos = await GetIndexVideos(tab, count, offset, connection, context);

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
				var video = await GetVideo(videoId, connection, context);
				var user = await UserSessions.GetLoggedInUser(context);

				if (video != null && UserCanViewVideo(video, user))
				{
					bool userOwnsVideo = false;
					var userPlaylists = new List<Playlist>();
					if (user != null)
					{
						userOwnsVideo = UserOwnsVideo(video, user.userid);
						userPlaylists = await GetPlaylistsWithCheckIfVideoIsInPlaylist(user.userid, video.id, connection, context);
					}

					var relatedVideos = new Dictionary<Guid, Video>();
					var count = 5;
					var videosWithId = await GetRelatedVideosWithUserid(connection, context, count, video.userid);
					var fuzzyVideos = (List<Video>)await FindVideosFuzzy(video.title.Replace(" ", " | "), count, connection, context);
					for (int i = 0; i < videosWithId.Count; i++)
					{

						relatedVideos[videosWithId[i].id] = (videosWithId[i]);
					}
					for (int i = 0; i < fuzzyVideos.Count; i++)
					{
						relatedVideos[fuzzyVideos[i].id] = (fuzzyVideos[i]);
					}


					var templateContext = new TemplateContext(new { video, relatedVideos = relatedVideos.Values, userOwnsVideo, userPlaylists });
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

				var numVideos = await NumVideosForUser(user.userid, connection, context);
				var VideosTask = VideosForUser(user.userid, countPerPage, offset, connection, context);

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
				var user = await UserController.UserFromUsername(username, connection, context);

				if (user != null)
				{
					const int countPerPage = 20;
					int page = 1;
					if (context.Request.Query.ContainsKey("page"))
					{
						Int32.TryParse(context.Request.Query["page"], out page);
					}

					int offset = (page - 1) * countPerPage;

					var numVideos = await NumPublicVideosForUser(user.userid, connection, context);
					var VideosTask = PublicVideosForUser(user.userid, countPerPage, offset, connection, context);

					var playlists = await GetPublicPlaylists(user.userid, connection, context);
					foreach(var playlist in playlists)
					{
						playlist.videos = await GetVideosOfPlaylist(playlist.id, connection, context);
					}

					var pagination = new Pagination(numVideos, countPerPage, offset);

					var templateContext = new TemplateContext(new { videos = await VideosTask, user, pagination, playlists });

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
				var video = await GetVideo(videoId, connection, context);
				if (UserOwnsVideo(video, user.userid))
				{
					var templateContext = new TemplateContext(new { video });
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
				var video = await GetVideo(videoId, connection, context);
				if (UserOwnsVideo(video, user.userid))
				{
					await DeleteVideo(video.id, connection, context);

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

		//TODO(Simon): redirect to login if not logged in. Add returnurl to login
		[Route("GET", "/edit_video")]
		private static async Task EditVideoGet(HttpContext context)
		{
			CommonController.SetHTMLContentType(context);

			var userTask = UserSessions.GetLoggedInUser(context);
			using var connection = Database.OpenNewConnection();
			var user = await userTask;

			if (user != null && GuidHelpers.TryDecode(context.Request.Query["id"], out var videoId))
			{
				var video = await GetVideo(videoId, connection, context);
				if (UserOwnsVideo(video, user.userid))
				{
					var templateContext = new TemplateContext(new { video });
					await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\editVideo.liquid", templateContext));
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

		[Route("POST", "/edit_video")]
		private static async Task EditVideoPost(HttpContext context)
		{
			CommonController.SetHTMLContentType(context);

			var userTask = UserSessions.GetLoggedInUser(context);
			using var connection = Database.OpenNewConnection();
			var user = await userTask;

			if (user != null && GuidHelpers.TryDecode(context.Request.Query["id"], out var videoId))
			{
				var video = await GetVideo(videoId, connection, context);
				if (UserOwnsVideo(video, user.userid))
				{
					var form = context.Request.Form;
					video.title = form["title"];
					video.description = form["description"];

					//TODO(Simon): Deduplicate tags. Should be cleaned by frontend, but may be malicious data.
					string[] tags = form["tags"].ToString().Split(',');
					var deduplicatedTags = new HashSet<string>(tags);
					video.tags = deduplicatedTags.ToList();
					if (Int32.TryParse(form["privacy"], out var privacyInt))
					{
						video.privacy = (VideoPrivacy)privacyInt;
					}

					await AddOrUpdateVideo(video, connection, context);

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
				var video = await GetVideo(videoid, connection, context);
				if (UserOwnsVideo(video, user.userid))
				{
					if (Int32.TryParse(context.Request.Form["video-privacy"], out int privacy))
					{
						await SetVideoPrivacy(video.id, (VideoPrivacy)privacy, connection, context);
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

				var channels = await FindUsersFuzzy(normalizedQuery, 3, connection, context);
				var videos = await FindVideosFuzzy(normalizedQuery, 20, connection, context);

				bool hasResults = channels.Any() || videos.Any();

				var templateContext = new TemplateContext(new { channels, videos, searchQuery, hasResults });
				await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\search.liquid", templateContext));
			}
			else
			{
				await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\search.liquid", null));
			}
		}

		[Route("POST", "/api/make_playlist")]
		private static async Task MakePlaylist(HttpContext context)
		{
			var userTask = UserSessions.GetLoggedInUser(context);
			using var connection = Database.OpenNewConnection();
			var user = await userTask;

			if (user != null)
			{
				string name = context.Request.Query["name"].ToString();
				VideoPrivacy privacy = VideoPrivacy.Public;
				if (Int32.TryParse(context.Request.Query["privacy"], out var privacyInt))
				{
					privacy = (VideoPrivacy)privacyInt;
				}

				GuidHelpers.TryDecode(context.Request.Query["videoid"].ToString(), out var videoid);

				var existingPlaylist = await GetPlaylistWithNameAndUserid(name, user.userid, connection, context);
				if (existingPlaylist == null)
				{
					bool success = await AddPlaylist(name, privacy, user.userid, connection, context);
					if (success)
					{
						var playlist = await GetPlaylistWithNameAndUserid(name, user.userid, connection, context);
						if (await AddVideoToPlaylist(playlist.id, videoid, connection, context))
						{
							await context.Response.Body.WriteAsync(Utf8Json.JsonSerializer.SerializeUnsafe($"Video has been added to { name }"));
						}
						else
						{
							await CommonController.WriteError(context, "Playlist is made, but something went wrong with the video", StatusCodes.Status500InternalServerError);
						}
					}
					else
					{
						await CommonController.WriteError(context, "Something went wrong", StatusCodes.Status500InternalServerError);
					}
				}
				else
				{

					await CommonController.WriteError(context, $"Playlist {name} allready exist", StatusCodes.Status500InternalServerError);

				}
			}
			else
			{
				await CommonController.Write404(context);
			}
		}

		[Route("GET", "/api/delete_video_from_playlist")]
		private static async Task DeleteVideoFromPlaylist(HttpContext context)
		{
			var userTask = UserSessions.GetLoggedInUser(context);
			using var connection = Database.OpenNewConnection();
			var user = await userTask;

			if (user != null)
			{
				GuidHelpers.TryDecode(context.Request.Query["playlistid"], out var playlistid);
				GuidHelpers.TryDecode(context.Request.Query["videoid"].ToString(), out var videoid);
				if (await UserOwnsPlaylist(playlistid, user.userid, connection, context)){
					if (await DeleteVideoFromPlaylist(playlistid, videoid, connection, context))
					{
						await context.Response.Body.WriteAsync(Utf8Json.JsonSerializer.SerializeUnsafe("Video is deleted from the playlist."));
					}
					else
					{
						await CommonController.WriteError(context, "Something went wrong", StatusCodes.Status500InternalServerError);
					}
				}
				else
				{
					await CommonController.WriteError(context, "Something went wrong", StatusCodes.Status500InternalServerError);
				}
			}
			else
			{
				await CommonController.Write404(context);
			}
		}

		[Route("POST", "/api/add_video_to_playlist")]
		private static async Task AddVideoToPlaylist(HttpContext context)
		{
			var userTask = UserSessions.GetLoggedInUser(context);
			using var connection = Database.OpenNewConnection();
			var user = await userTask;

			if (user != null)
			{
				GuidHelpers.TryDecode(context.Request.Query["playlistid"], out var playlistid);
				GuidHelpers.TryDecode(context.Request.Query["videoid"].ToString(), out var videoid);

				if (await UserOwnsPlaylist(playlistid, user.userid, connection, context))
				{
					if(await IsThereRoomInPlaylist(playlistid, connection, context))
					{
						if (await AddVideoToPlaylist(playlistid, videoid, connection, context))
						{
							await context.Response.Body.WriteAsync(Utf8Json.JsonSerializer.SerializeUnsafe("Video is added to the playlist"));

						}
						else
						{
							await CommonController.WriteError(context, "Something went wrong", StatusCodes.Status500InternalServerError);
						}
					}
					else
					{
						await CommonController.WriteError(context, "Playlist is full", StatusCodes.Status500InternalServerError);
					}
				}
				else
				{
					await CommonController.WriteError(context, "Something went wrong", StatusCodes.Status500InternalServerError);
				}
			}
			else
			{
				await CommonController.Write404(context);
			}
		}

		[Route("GET", "/my_playlists")]
		private static async Task GetMyPlaylists(HttpContext context)
		{
			CommonController.SetHTMLContentType(context);

			var userTask = UserSessions.GetLoggedInUser(context);
			using var connection = Database.OpenNewConnection();
			var user = await userTask;

			if (user != null)
			{
				var playlists = await GetPlaylistsOfUser(user.userid, connection, context);
				foreach (var playlist in playlists)
				{
					playlist.videos = await GetVideosOfPlaylist(playlist.id, connection, context);
				}
				var templateContext = new TemplateContext(new { playlists });

				await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\myPlaylists.liquid", templateContext));
			}
			else
			{
				await CommonController.Write404(context);
			}
		}

		[Route("GET", "/delete_playlist")]
		private static async Task DeletePlaylistGet(HttpContext context)
		{
			CommonController.SetHTMLContentType(context);

			var userTask = UserSessions.GetLoggedInUser(context);
			using var connection = Database.OpenNewConnection();
			var user = await userTask;

			if (user != null && GuidHelpers.TryDecode(context.Request.Query["id"], out var playlistid))
			{
				if(await UserOwnsPlaylist(playlistid, user.userid, connection, context))
				{
					var playlist = await GetPlaylistWithId(playlistid, connection, context);
					var videos = await GetVideosOfPlaylist(playlistid, connection, context);

					var templateContext = new TemplateContext(new { playlist, videos });
					await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\deletePlaylistConfirm.liquid", templateContext));
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

		[Route("GET", "/delete_playlist_confirm")]
		private static async Task DeletePlaylistConfirmGet(HttpContext context)
		{
			CommonController.SetHTMLContentType(context);

			var userTask = UserSessions.GetLoggedInUser(context);
			using var connection = Database.OpenNewConnection();
			var user = await userTask;

			if (user != null && GuidHelpers.TryDecode(context.Request.Query["id"], out var playlistid))
			{
				if(await UserOwnsPlaylist(playlistid, user.userid, connection, context))
				{
					await DeletePlaylist(playlistid, user.userid, connection, context);
					context.Response.Redirect("/my_playlists");
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

		[Route("GET", "/api/get_playlists_with_video_check")]
		private static async Task GetPlaylistsWithVideoCheck(HttpContext context)
		{
			var userTask = UserSessions.GetLoggedInUser(context);
			using var connection = Database.OpenNewConnection();
			var user = await userTask;

			if (user != null)
			{
				GuidHelpers.TryDecode(context.Request.Query["videoid"].ToString(), out var videoid);

				var userPlaylists = await GetPlaylistsWithCheckIfVideoIsInPlaylist(user.userid, videoid, connection, context);
				await context.Response.Body.WriteAsync(Utf8Json.JsonSerializer.SerializeUnsafe(userPlaylists));
			}
			else
			{
				await CommonController.Write404(context);
			}
		}

		[Route("POST", "/update_playlist_privacy")]
		private static async Task UpdatePlaylistPrivacy(HttpContext context)
		{
			CommonController.SetHTMLContentType(context);

			var userTask = UserSessions.GetLoggedInUser(context);
			using var connection = Database.OpenNewConnection();
			var user = await userTask;

			if (user != null && GuidHelpers.TryDecode(context.Request.Query["id"], out var playlistid))
			{
				if (await UserOwnsPlaylist(playlistid, user.userid, connection, context))
				{
					if (Int32.TryParse(context.Request.Form["playlist-privacy"], out int privacy))
					{
						await SetPlaylistPrivacy(playlistid, (VideoPrivacy)privacy, connection, context);
					}
				}
			}

			context.Response.Redirect("/my_playlists");
		}

		[Route("GET", "/detail_playlist")]
		private static async Task DetailPlaylist(HttpContext context)
		{
			CommonController.SetHTMLContentType(context);

			var userTask = UserSessions.GetLoggedInUser(context);
			using var connection = Database.OpenNewConnection();
			var user = await userTask;

			if (GuidHelpers.TryDecode(context.Request.Query["id"], out var playlistid))
			{
				var playlist = await GetPlaylistWithId(playlistid, connection, context);
				playlist.idBase64 = context.Request.Query["id"];
				if (user != null && await UserOwnsPlaylist(playlistid, user.userid, connection, context))
				{
					playlist.videos = await GetVideosOfPlaylist(playlist.id, connection, context);
					var templateContext = new TemplateContext(new { playlist, IsOwner = true });
					await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\detailPlaylist.liquid", templateContext));
				}
				else if(playlist.privacy == VideoPrivacy.Public)
				{
					playlist.videos = await GetVideosOfPlaylist(playlist.id, connection, context);
					var templateContext = new TemplateContext(new { playlist, IsOwner = false });
					await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\detailPlaylist.liquid", templateContext));
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

		[Route("POST", "/api/edit_playlist_order")]
		private static async Task EditPlaylistOrder(HttpContext context)
		{
			var userTask = UserSessions.GetLoggedInUser(context);
			using var connection = Database.OpenNewConnection();
			var user = await userTask;

			if (user != null && GuidHelpers.TryDecode(context.Request.Query["playlistid"], out var playlistid) && GuidHelpers.TryDecode(context.Request.Query["video1"], out var video1) && GuidHelpers.TryDecode(context.Request.Query["video2"], out var video2))
			{
				if (await UserOwnsPlaylist(playlistid, user.userid, connection, context))
				{
					var playlist = await GetPlaylistWithId(playlistid, connection, context);
					int indexVideo1 = 0;
					int indexVideo2 = 0;

					playlist.videos = await GetVideosOfPlaylist(playlistid, connection, context);

					int counter = 0;
					if (playlist.videos.Exists(video => video.id == video1) && playlist.videos.Exists(video => video.id == video2))
					{
						foreach (var video in playlist.videos)
						{
							if (video.id == video1)
							{
								indexVideo1 = counter;
							}
							if (video.id == video2)
							{
								indexVideo2 = counter;
							}
							counter++;
						}

						var temp = playlist.videos[indexVideo1];
						playlist.videos.Remove(temp);
						playlist.videos.Insert(indexVideo2, temp);

						var maxIndex = await GetMaxIndexOfPlaylist(playlistid, connection, context) + 1;
						foreach (var video in playlist.videos)
						{
							await SetPlaylistVideosIndex(playlistid, video.id, maxIndex, connection, context);
							maxIndex++;
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
			else
			{
				await CommonController.Write404(context);
			}
		}

		public static async Task<bool> AddPlaylist(string name, VideoPrivacy privacy, int userid, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				if (name.Length <= 0)
				{
					return false;
				}
				await Database.ExecuteAsync(connection,
											@"INSERT INTO playlists (id, name, userid, privacy)
												VALUES (@id::uuid, @name, @userid, @privacy)",
												context,
												new { id = Guid.NewGuid(), name, userid, privacy });
				return true;
			}
			catch (Exception e)
			{
				return false;
			}
		}
		public static async Task<bool> UserOwnsPlaylist(Guid playlistid, int userid, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				var playlist = await Database.QuerySingleAsync<Playlist>(connection,
											@"SELECT * FROM playlists WHERE userid = @userid AND id = @playlistid::uuid;",
												context,
												new { userid, playlistid });
				return playlist != null;
			}
			catch (Exception e)
			{
				return false;
			}
		}

		public static async Task<bool> AddVideoToPlaylist(Guid playlistid, Guid videoid, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				var indexes = await Database.QueryAsync<int>(connection, @"SELECT index FROM playlist_videos WHERE playlistid = @playlistid::uuid", context, new { playlistid });
				var index = 0;
				if (indexes.Count() > 0)
				{
					index = indexes.Max();
				}

				index++;
				await Database.ExecuteAsync(connection,
										@"INSERT INTO playlist_videos (playlistid, videoid, index)
												VALUES (@playlistid::uuid, @videoid, @index)",
											context,
											new { playlistid, videoid, index });
				await Database.ExecuteAsync(connection,
								@"UPDATE  playlists 
											SET count = count + 1
												WHERE id = @playlistid::uuid",
									context,
									new { playlistid });

				return true;
			}
			catch (Exception e)
			{
				return false;
			}
		}

		public static async Task<Playlist> GetPlaylistWithNameAndUserid(string name, int userid, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				var playlist = await Database.QuerySingleAsync<Playlist>(connection,
											@"SELECT * FROM playlists WHERE userid = @userid AND name = @name;",
												context,
												new { userid, name });
				return playlist;
			}
			catch (Exception e)
			{
				return null;
			}
		}

		public static async Task<List<Playlist>> GetPlaylistsOfUser(int userid, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				var playlists = await Database.QueryAsync<Playlist>(connection,
											@"SELECT * FROM playlists WHERE userid = @userid ORDER BY name ",
												context,
												new { userid });
				return playlists.ToList();
			}
			catch (Exception e)
			{
				return new List<Playlist>();
			}
		}

		public static async Task<bool> IsVideoInPlaylist(Guid playlistid, Guid videoid, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				var playlist_video = await Database.QuerySingleAsync<Playlist>(connection,
											@"SELECT * FROM playlist_videos WHERE playlistid = @playlistid::uuid AND videoid = @videoid::uuid;",
												context,
												new { playlistid, videoid });
				return true;
			}
			catch (Exception e)
			{
				return false;
			}
		}

		public static async Task<bool> DeleteVideoFromPlaylist(Guid playlistid, Guid videoid, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				await Database.ExecuteAsync(connection,
											@"DELETE FROM playlist_videos
												WHERE playlistid = @playlistid::uuid AND videoid = @videoid::uuid;",
												context,
												new { playlistid, videoid });
				await Database.ExecuteAsync(connection,
									@"UPDATE  playlists 
											SET count = count - 1
												WHERE id = @playlistid::uuid",
					context,
					new { playlistid });
				return true;
			}
			catch (Exception e)
			{
				return false;
			}
		}
		public static async Task<List<Video>> GetVideosOfPlaylist(Guid playlistid, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				var videos = await Database.QueryAsync<Video>(connection,
											@"SELECT v.*, u.username FROM videos v 
											INNER JOIN playlist_videos p on p.videoid = v.id 
											INNER JOIN users u on u.userid = v.userid
											WHERE playlistid = @playlistid::uuid
											ORDER BY p.index;",
												context,
												new { playlistid });
				return videos.ToList();
			}
			catch (Exception e)
			{
				return new List<Video>();
			}
		}

		public static async Task<bool> DeletePlaylist(Guid playlistid, int userid, NpgsqlConnection connection, HttpContext context)
		{
			try
			{

				await Database.ExecuteAsync(connection,
														@"delete from playlist_videos
															where playlistid=@playlistid::uuid",
															context,
														new { playlistid, userid });

				await Database.ExecuteAsync(connection,
														@"delete from playlists
															where id=@playlistid::uuid
															AND userid = @userid",
															context,
														new { playlistid, userid });

				return true;
			}
			catch (Exception e)
			{
				return false;

			}
		}
		public static async Task<Playlist> GetPlaylistWithId(Guid playlistid, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				var playlist = await Database.QuerySingleAsync<Playlist>(connection,
											@"SELECT * FROM playlists WHERE id = @playlistid::uuid;",
												context,
												new { playlistid });
				return playlist;
			}
			catch (Exception e)
			{
				return null;
			}
		}

		private static async Task<List<Playlist>> GetPlaylistsWithCheckIfVideoIsInPlaylist(int userid, Guid videoid, NpgsqlConnection connection, HttpContext context)
		{
			var userPlaylists = await GetPlaylistsOfUser(userid, connection, context);
			foreach (var playlist in userPlaylists)
			{
				playlist.playListContainsVideo = await IsVideoInPlaylist(playlist.id, videoid, connection, context);
				playlist.idBase64 = GuidHelpers.Encode(playlist.id);
			}
			return userPlaylists;
		}
		
		private static async Task<bool> SetPlaylistPrivacy(Guid playlistid, VideoPrivacy privacy, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				var success = await Database.ExecuteAsync(connection,
															@"update playlists
															set privacy=@privacy
															where id=@playlistid::uuid",
															context,
														new { playlistid, privacy = (int)privacy });

				return success > 0;
			}
			catch (Exception e)
			{
				return false;
			}
		}

		private static async Task<bool> IsThereRoomInPlaylist(Guid playlistid, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				var playlist = await Database.QuerySingleAsync<Playlist>(connection,
											@"SELECT * FROM playlists WHERE id = @playlistid::uuid;",
												context,
												new { playlistid });
				return playlist.count < 50;
			}
			catch (Exception e)
			{
				return false;
			}
		}

		private static async Task<List<Playlist>> GetPublicPlaylists(int userid, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				var publicPlaylists = await Database.QueryAsync<Playlist>(connection, @"SELECT p.*, u.username FROM playlists p
																							INNER JOIN users u on u.userid = p.userid
																							WHERE p.userid = @userid AND p.privacy = @privacy",
																							context,
																							new { userid, privacy = VideoPrivacy.Public });
				return publicPlaylists.ToList();
			}
			catch(Exception e)
			{
				return new List<Playlist>();
			}
		}

		public static async Task<int> GetMaxIndexOfPlaylist(Guid playlistid, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				var index = await Database.QuerySingleAsync<int>(connection,
											@"SELECT MAX(index) FROM playlist_videos WHERE playlistid = @playlistid::uuid;",
												context,
												new { playlistid });
				return index;
			}
			catch (Exception e)
			{
				return -1;
			}
		}

		private static async Task<bool> SetPlaylistVideosIndex(Guid playlistid, Guid videoid, int index, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				var success = await Database.ExecuteAsync(connection,
															@"update playlist_videos
															set index=@index
															where playlistid=@playlistid::uuid AND videoid=@videoid::uuid",
															context,
														new { playlistid, videoid, index });

				return success > 0;
			}
			catch (Exception e)
			{
				return false;
			}
		}

		public static async Task AuthorizeUploadTus(AuthorizeContext arg)
		{
			CommonController.LogDebug("Authorizing upload request");
			var user = await UserSessions.GetLoggedInUser(arg.HttpContext);
			if (user != null)
			{
				var video = new Video
				{
					id = new Guid(arg.HttpContext.Request.Headers["guid"]),
					userid = user.userid,
					privacy = VideoPrivacy.Processing,
				};

				CommonController.LogDebug("Video metadata:");
				CommonController.LogDebug($"\t id: {video.id}");
				CommonController.LogDebug($"\t userid: {video.userid}");
				CommonController.LogDebug($"\t privacy: {video.privacy}");


				var cachedOwner = (User)uploadAuthorisationCache[video.id.ToString()];
				bool exists;
				bool owns;

				if (cachedOwner == null)
				{
					CommonController.LogDebug("No owner found in cache");
					using var connection = Database.OpenNewConnection();
					exists = await VideoExists(video.id, connection, arg.HttpContext);
					owns = await UserOwnsVideo(video.id, user.userid, connection, arg.HttpContext);
					await AddOrUpdateVideo(video, connection, arg.HttpContext);
				}
				//NOTE(Simon): At this point the video has definitely been created, so it exists and is owned by the cached user
				else if (cachedOwner.userid == user.userid)
				{
					CommonController.LogDebug("User is owner of video. allow upload");
					exists = true;
					owns = true;
				}
				else
				{
					CommonController.LogDebug("User not authorized to update this video");
					arg.FailRequest("This user is not authorized to update this video");
					return;
				}

				if (exists && owns || !exists)
				{
					CommonController.LogDebug("Adding user to cache");
					uploadAuthorisationCache.Add(video.id.ToString(), user, new CacheItemPolicy { SlidingExpiration = TimeSpan.FromMinutes(10) });
					return;
				}
			}

			arg.FailRequest("This user is not authorized to update this video");
			CommonController.LogDebug("User not authorized to upload videos");
		}

		public static async Task ProcessUploadTus(FileCompleteContext arg)
		{
			CommonController.LogDebug("Begin processing upload");
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
						CommonController.LogDebug("Upload has correct headers");
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

						CommonController.LogDebug($"Creating directory for {newFilePath} and moving uploaded file there");
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



		public static async Task<IEnumerable<Video>> VideosForUser(int userid, int count, int offset, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				var videos = await Database.QueryAsync<Video>(connection,
															@"select * from videos
																where userid=@userid
																order by timestamp desc
																limit @count
																offset @offset", context, new { userid, count, offset });

				return videos;
			}
			catch (Exception e)
			{
				return new List<Video>();
			}
		}

		public static async Task<IEnumerable<Video>> PublicVideosForUser(int userid, int count, int offset, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				var videos = await Database.QueryAsync<Video>(connection,
																@"select * from videos
																where userid=@userid and privacy=@privacy
																order by timestamp desc
																limit @count
																offset @offset", context, new { userid, count, offset, privacy = VideoPrivacy.Public });

				return videos;
			}
			catch (Exception e)
			{
				return new List<Video>();
			}
		}

		private static async Task<int> NumVideosForUser(int userid, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				int totalcount = await Database.QuerySingleAsync<int>(connection,
																	@"select count(*) from videos
																		where userid=@userid", context, new { userid });
				return totalcount;
			}
			catch (Exception e)
			{
				return 0;
			}
		}

		public static async Task<int> NumPublicVideosForUser(int userid, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				int totalcount = await Database.QuerySingleAsync<int>(connection,
																	@"select count(*) from videos
																		where userid=@userid and privacy=@privacy",
																		context,
																		new { userid, privacy = VideoPrivacy.Public });
				return totalcount;
			}
			catch (Exception e)
			{
				return 0;
			}
		}

		private static async Task<bool> UserOwnsVideo(Guid guid, int userId, NpgsqlConnection connection, HttpContext context)
		{
			int count;
			try
			{
				count = await Database.QuerySingleAsync<int>(connection,
																@"select count(*) from videos where id=@guid and userid=@userId",
																context,
																new { guid, userId });
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

		private static bool UserCanViewVideo(Video video, User user)
		{
			switch (video.privacy)
			{
				case VideoPrivacy.Public:
				case VideoPrivacy.Unlisted:
				case VideoPrivacy.Private when video.userid == user?.userid:
					return true;
				default:
					return false;
			}
		}

		private static async Task<bool> VideoExists(Guid guid, NpgsqlConnection connection, HttpContext context)
		{
			int count;
			try
			{
				count = await Database.QuerySingleAsync<int>(connection,
																@"select count(*) from videos where id=@guid",
																context,
																new { guid });
			}
			catch
			{
				return false;
			}

			return count > 0;
		}

		private static async Task<Video> GetVideo(Guid videoid, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				var video = await Database.QuerySingleAsync<Video>(connection,
													@"select v.*, u.username, u.pictureid as userpicture from videos v
													inner join users u on v.userid = u.userid
													where v.id=@videoid::uuid",
													context,
													new { videoid });

				return video;
			}
			catch (Exception e)
			{
				return null;
			}
		}

		private static async Task<IEnumerable<Video>> GetIndexVideos(IndexTab tab, int count, int offset, NpgsqlConnection connection, HttpContext context)
		{
			if (tab == IndexTab.New)
			{
				return await Database.QueryAsync<Video>(connection,
							@"select v.*, u.username from videos v
								inner join users u on v.userid = u.userid
								where privacy=@privacy
								order by v.timestamp desc
								limit @count
								offset @offset",
								context,
							new { count, offset, privacy = VideoPrivacy.Public });
			}

			if (tab == IndexTab.MostWatched)
			{
				return await Database.QueryAsync<Video>(connection,
								@"select v.*, u.username from videos v
								inner join users u on v.userid = u.userid
								where privacy=@privacy
								order by v.views desc
								limit @count
								offset @offset",
								context,
								new { count, offset, privacy = VideoPrivacy.Public });
			}

			if (tab == IndexTab.Popular)
			{
				return await Database.QueryAsync<Video>(connection,
							@"select v.*, u.username from videos v
								inner join users u on v.userid = u.userid
								where privacy=@privacy
								order by v.views desc
								limit @count
								offset @offset",
								context,
							new { count, offset, privacy = VideoPrivacy.Public });
			}

			return null;
		}

		private static async Task<bool> AddOrUpdateVideo(Video video, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				var timestamp = DateTime.UtcNow;
				await Database.ExecuteAsync(connection,
											@"INSERT INTO videos (id, userid, title, description, length, downloadsize, timestamp, privacy)
												VALUES (@id::uuid, @userid, @title, @description, @length, @downloadsize, @timestamp, @privacy)
												ON CONFLICT(id) DO UPDATE
												SET title=@title, description=@description, length=@length, downloadsize=@downloadsize, privacy=@privacy",
												context,
												new { video.id, video.userid, video.title, video.description, video.length, video.downloadsize, timestamp, video.privacy });

				if (video.tags != null && video.tags.Count > 0)
				{

				}

				return true;
			}
			catch (Exception e)
			{
				return false;
			}
		}

		private static async Task<bool> SetVideoPrivacy(Guid videoid, VideoPrivacy privacy, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				var success = await Database.ExecuteAsync(connection,
															@"update videos
															set privacy=@privacy
															where id=@videoid::uuid",
															context,
														new { videoid, privacy = (int)privacy });

				return success > 0;
			}
			catch (Exception e)
			{
				return false;
			}
		}

		private static async Task<bool> AddVideoView(Guid videoid, HttpContext context, NpgsqlConnection connection)
		{
			DashboardController.AddViews();
			var ip = context.Connection.RemoteIpAddress;

			bool inCache = viewHistoryCache.Get(ip.ToString()) != null;

			if (!inCache)
			{
				try
				{
					var success = await Database.ExecuteAsync(connection,
														@"update videos
															set views = views + 1
															where id=@videoid::uuid",
															context,
														new { videoid });

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

		private static async Task<bool> AddVideoDownload(Guid videoid, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				var success = await Database.ExecuteAsync(connection,
														@"update videos
															set downloads = downloads + 1
															where id=@videoid:uuid",
															context,
														new { videoid });
				DashboardController.AddDownloads();
				return success > 0;
			}
			catch (Exception e)
			{
				return false;
			}
		}

		private static async Task<bool> DeleteVideo(Guid videoid, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				var result = await Database.ExecuteAsync(connection,
														@"delete from videos
															where id=@videoid::uuid",
															context,
														new { videoid });

				uploadAuthorisationCache.Remove(videoid.ToString());

				return result > 0;
			}
			catch (Exception e)
			{
				return false;
			}
		}

		//NOTE(Simon): Perform a fuzzy search for user, based on a trigram index on usernames
		private static async Task<IEnumerable<User>> FindUsersFuzzy(string query, int count, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				var result = await Database.QueryAsync<User>(connection,
															@"SELECT *
															  from users
															  where username % @query
															  order by similarity(username, @query) desc, username
															  limit @count",
																context,
															new { query, count });

				return result;
			}
			catch (Exception e)
			{
				return new List<User>();
			}
		}

		private async static Task<List<Video>> GetRelatedVideosWithUserid(NpgsqlConnection connection, HttpContext context, int count, int userid)
		{
			var videos = await Database.QueryAsync<Video>(connection,
									@"select v.*, u.username from videos v
													inner join users u on v.userid = u.userid
													where v.userid=@userid
													limit @count",
									context,
									new { userid, count });
			return videos.ToList();
		}

		//NOTE(Simon): Perform a fuzzy search for videos, based on a trigram index
		private static async Task<IEnumerable<Video>> FindVideosFuzzy(string query, int count, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				var result = await Database.QueryAsync<Video>(connection,
															@"select ts_rank(search, websearch_to_tsquery('english', @query)) as rank, v.*, u.username
																from videos v
																inner join users u on v.userid = u.userid
																where search @@ to_tsquery('english', @query) and privacy = privacy
																order by rank
																limit @count",
																context,
																new { query, count, privacy = VideoPrivacy.Public });
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
			CommonController.LogDebug("Getting video length with ffprobe");
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
			CommonController.LogDebug($"ffprobe exit code {process.ExitCode}");
			if (process.ExitCode == 0)
			{
				CommonController.LogDebug($"ffprobe run succesful");
				return (int)float.Parse(process.StandardOutput.ReadLine());
			}

			return 0;
		}

		public static int GetItemsInUploadCache()
		{
			return (int)uploadAuthorisationCache.GetCount();
		}
	}
}
