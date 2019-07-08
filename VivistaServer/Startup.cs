using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Dapper;
using Npgsql;

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

		private static readonly PathString indexURL = new PathString("/");
		private static readonly PathString registerURL = new PathString("/register");
		private static readonly PathString loginURL = new PathString("/login");
		private static readonly PathString videoURL = new PathString("/video");
		private static readonly PathString metaURL = new PathString("/meta");
		private static readonly PathString extraURL = new PathString("/extra");
		private static readonly PathString allExtrasURL = new PathString("/extras");
		private static readonly PathString thumbnailURL = new PathString("/thumbnail");

		private static RNGCryptoServiceProvider rng;

		private const int indexCountDefault = 10;
		private const int bcryptWorkFactor = 12;
		private const int sessionLength = 1 * 24 * 60;

		private const int fileBufferSize = 16 * kb;
		private const int kb = 1024;
		private const int mb = 1024 * kb;
		private const int gb = 1024 * mb;

		private const string baseFilePath = @"C:\test\";

		//NOTE(Simon): Use GetPgsqlConfig() instead of this directly, it handles caching of this variable.
		private static string connectionString;

		private Dictionary<string, string> supportedExtensions = new Dictionary<string, string>
		{
			{".html", "text/html"},
			{".js", "application/javascript"},
			{".css", "text/css"},
			{".png", "image/png"},
			{".jpeg", "image/jpeg"},
			{".jpg", "image/jpeg"},
			{".mp4", "video/mpeg"},
			{".gif", "gif"},
		};

		// This method gets called by the runtime. Use this method to add services to the container.
		// For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
		public void ConfigureServices(IServiceCollection services)
		{

		}

		// This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
		public void Configure(IApplicationBuilder app, IHostingEnvironment env)
		{
			if (env.IsDevelopment())
			{
				app.UseDeveloperExceptionPage();
			}

			rng = new RNGCryptoServiceProvider();
			Task.Run(PeriodicFunction);

			app.Run(async (context) =>
			{
#if DEBUG
				Console.WriteLine("Request data:");
				Console.WriteLine("\tPath: " + context.Request.Path);
				Console.WriteLine("\tMethod: " + context.Request.Method);
				Console.WriteLine("\tQuery: ");
				foreach (var kvp in context.Request.Query)
				{
					Console.WriteLine($"\t\t{kvp.Key}: {kvp.Value.ToString()}");
				}
				Console.WriteLine("\tHeaders: ");
				foreach (var kvp in context.Request.Headers)
				{
					Console.WriteLine($"\t\t{kvp.Key}: {kvp.Value.ToString()}");
				}
				if (!context.Request.HasFormContentType)
				{
					Console.WriteLine("\tBody: " + new StreamReader(context.Request.Body).ReadToEnd());
				}
#endif

				var path = context.Request.Path;
				bool isGet = context.Request.Method == "GET";
				bool isPost = context.Request.Method == "POST";

				context.Response.ContentType = "application/json";

				var connection = new NpgsqlConnection(GetPgsqlConfig());
				connection.Open();

				if (isGet)
				{
					if (MatchPath(path, indexURL))
					{
						await IndexGet(context, connection).ConfigureAwait(false);
					}
					else if (MatchPath(path, videoURL))
					{
						await VideoGet(context, connection);
					}
					else if (MatchPath(path, metaURL))
					{
						await MetaGet(context);
					}
					else if (MatchPath(path, extraURL))
					{
						await ExtraGet(context);
					}
					else if (MatchPath(path, allExtrasURL))
					{
						await ExtrasGet(context, connection);
					}
					else if (MatchPath(path, thumbnailURL))
					{
						await ThumbnailGet(context);
					}
					else
					{
						await StaticFileGet(context, env);
					}
				}
				else if (isPost)
				{
					if (MatchPath(path, registerURL))
					{
						await RegisterPost(context, connection);
					}
					else if (MatchPath(path, loginURL))
					{
						await LoginPost(context, connection);
					}
					else if (MatchPath(path, videoURL))
					{
						await VideoPost(context, connection).ConfigureAwait(false);
					}
					else if (MatchPath(path, allExtrasURL))
					{
						await ExtrasPost(context, connection);
					}
					else
					{
						await Write404(context);
					}
				}
				else
				{
					await Write404(context);
				}

				connection.Close();
			});
		}



		private async Task PeriodicFunction()
		{
			while (true)
			{
				Console.WriteLine("periodic");
				await Task.Delay(5000);
			}
		}

		private async Task StaticFileGet(HttpContext context, IHostingEnvironment env)
		{
			var rawPath = context.Request.Path.Value;

			var filename = rawPath.Substring(rawPath.IndexOf("/") + 1);
			var extension = rawPath.Substring(rawPath.LastIndexOf("."));
			var supported = supportedExtensions.Keys.Contains(extension);


			if (!String.IsNullOrEmpty(extension))
			{
				if (supported)
				{
					context.Response.ContentType = supportedExtensions[extension];
					var filepath = Path.Join("static", filename);
					if (File.Exists(filepath))
					{
						if (extension == ".html" && filename != "main.html")
						{
							var templateFilename = Path.Join("static", "template.html");
							var template = File.ReadAllText(templateFilename);
							var content = File.ReadAllText(filepath);
							var result = ReplaceContent(template, content);
							await context.Response.WriteAsync(result);
						}
						else
						{
							await context.Response.SendFileAsync(env.ContentRootFileProvider.GetFileInfo(filepath));
						}
						return;
					}
					else
					{
						await Write404(context, "File not found");
						return;
					}
				}
				else
				{
					await WriteError(context, "Extension not supported", StatusCodes.Status415UnsupportedMediaType);
					return;
				}
			}

		}
		public string ReplaceContent(string template, string content)
		{
			return template.Replace("<&content&>", content);
		}

		private async Task IndexGet(HttpContext context, NpgsqlConnection connection)
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

			//TODO(Simon): Fuzzy search for username
			if (!String.IsNullOrEmpty(author))
			{
				userid = await GetUserId(author, connection);
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
				return;
			}
		}

		private async Task VideoGet(HttpContext context, NpgsqlConnection connection)
		{
			var args = context.Request.Query;
			var videoid = args["videoid"].ToString();

			if (String.IsNullOrEmpty(videoid))
			{
				await Write404(context);
				return;
			}

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
					return;
				}
			}
			else
			{
				await Write404(context);
				return;
			}
		}

		//TODO(Simon): Switch to query string, instead of path for id
		private async Task MetaGet(HttpContext context)
		{
			string path = context.Request.Path.Value;
			var id = path.Substring(path.LastIndexOf('/') + 1);
			if (id[id.Length - 1] == '/')
			{
				id = id.Substring(0, id.Length - 1);
			}

			if (Guid.TryParse(id, out var _))
			{
				string filename = Path.Combine(baseFilePath, id, "meta.json");
				await WriteFile(context, filename, "application/json", "meta.json");
				return;
			}
			else
			{
				await Write404(context);
				return;
			}
		}

		//TODO(Simon): Switch to query string, instead of path for id
		private async Task ThumbnailGet(HttpContext context)
		{
			string path = context.Request.Path.Value;
			var id = path.Substring(path.LastIndexOf('/') + 1);
			if (id[id.Length - 1] == '/')
			{
				id = id.Substring(0, id.Length - 1);
			}

			if (Guid.TryParse(id, out var _))
			{
				string filename = Path.Combine(baseFilePath, id, "thumb.jpg");
				await WriteFile(context, filename, "image/jpg", "thumb.jpg");
				return;
			}
			else
			{
				await Write404(context);
				return;
			}
		}

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
				return;
			}
			else
			{
				await Write404(context);
				return;
			}
		}

		private async Task ExtrasGet(HttpContext context, NpgsqlConnection connection)
		{
			var args = context.Request.Query;
			string idstring = args["videoid"];

			if (String.IsNullOrEmpty(idstring))
			{
				await Write404(context);
				return;
			}

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
					return;
				}
			}
		}

		private async Task RegisterPost(HttpContext context, NpgsqlConnection connection)
		{
			var form = context.Request.Form;

			string username = form["username"].ToString().ToLowerInvariant().Trim();
			string password = form["password"].ToString(); 

			if (username.Length == 0)
			{
				await WriteError(context, "Username too short", StatusCodes.Status400BadRequest);
				return;
			}
			if (password.Length == 0)
			{
				await WriteError(context, "Password too short", StatusCodes.Status400BadRequest);
				return;
			}

			var userExists = await UserExists(username, connection);
			var hashedPassword = BCrypt.Net.BCrypt.HashPassword(password, bcryptWorkFactor);

			if (!userExists)
			{
				try
				{
					int success = await connection.ExecuteAsync("insert into users (username, pass) values (@username, @hashedPassword)", new { username, hashedPassword });

					if (success == 0)
					{
						await WriteError(context, "Something went wrong while registering", StatusCodes.Status500InternalServerError);
						return;
					}
				}
				catch (Exception e)
				{
					await WriteError(context, "Something went wrong while registering", StatusCodes.Status500InternalServerError, e);
					return;
				}
			}
			else
			{
				await WriteError(context, "This user already exists", StatusCodes.Status409Conflict);
				return;
			}

			//NOTE(Simon): Create session token to immediately log user in.
			{
				var token = NewSessionToken(32);
				var expiry = DateTime.UtcNow.AddMinutes(sessionLength);
				var userid = await GetUserId(username, connection);

				if (userid == -1)
				{
					await WriteError(context, "Something went wrong while logging in", StatusCodes.Status500InternalServerError);
					return;
				}

				try
				{
					await connection.ExecuteAsync("insert into sessions (token, expiry, userid) values (@token, @expiry, @userid)", new { token, expiry, userid });
				}
				catch (Exception e)
				{
					await WriteError(context, "Something went wrong while logging in", StatusCodes.Status500InternalServerError, e);
					return;
				}
				//TODO(Simon): Wrap in JSON. Look for other occurences in file
				await context.Response.WriteAsync(token);
			}
		}

		private async Task LoginPost(HttpContext context, NpgsqlConnection connection)
		{
			var args = context.Request.Query;

			string username = args["username"].ToString().ToLowerInvariant().Trim();
			string password = args["password"];
			bool success = await AuthenticateUser(username, password, connection);

			if (success)
			{
				string token = NewSessionToken(32);
				var expiry = DateTime.UtcNow.AddMinutes(sessionLength);
				var userid = await GetUserId(username, connection);

				try
				{
					await connection.ExecuteAsync("delete from sessions where userid = @userId", new { userid });
					await connection.ExecuteAsync("insert into sessions (token, expiry, userid) values (@token, @expiry, @userId)", new { token, expiry, userid });
				}
				catch (Exception e)
				{
					await WriteError(context, "Something went wrong while processing this request", StatusCodes.Status500InternalServerError, e);
				}

				//TODO(Simon): Wrap in JSON. Look for other occurences in file
				await context.Response.WriteAsync(token);
			}
			else
			{
				await WriteError(context, "{}", StatusCodes.Status401Unauthorized);
			}
		}

		private async Task VideoPost(HttpContext context, NpgsqlConnection connection)
		{
			var form = context.Request.Form;
			string token = form["token"];

			if (await AuthenticateToken(token, connection))
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
					var userIdTask = GetUserIdFromToken(token, connection);
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
					return;
				}
				catch (Exception e)
				{
					//NOTE(Simon): If upload fails, just delete everything so we can start fresh next time.
					//TODO(Simon): Look into supporting partial uploads
					Directory.Delete(basePath, true);
					await WriteError(context, "Something went wrong while uploading this file", StatusCodes.Status500InternalServerError, e);
					return;
				}
			}
			else
			{
				await WriteError(context, "{}", StatusCodes.Status401Unauthorized);
				return;
			}
		}

		private async Task ExtrasPost(HttpContext context, NpgsqlConnection connection)
		{
			var form = context.Request.Form;
			string token = form["token"];
			string videoGuid = form["videoguid"];
			string rawExtraGuids = form["extraguids"];
			var extraGuids = rawExtraGuids.Split(',');

			if (await AuthenticateToken(token, connection))
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
					var downloadSize = await downloadSizeTask;
					await connection.ExecuteAsync("update videos set (downloadsize) = (@downloadSize) where id = @videoGuid::uuid", new { videoGuid, downloadsize = downloadSize });

					await context.Response.WriteAsync("{}");
					return;
				}
				catch (Exception e)
				{
					//NOTE(Simon): If upload fails, just delete everything so we can start fresh next time.
					//TODO(Simon): Look into supporting partial uploads
					Directory.Delete(basePath, true);
					await WriteError(context, "Something went wrong while uploading this file", StatusCodes.Status500InternalServerError, e);
					return;
				}
			}
			else
			{
				await WriteError(context, "{}", StatusCodes.Status401Unauthorized);
				return;
			}
		}



		private static string GetPgsqlConfig()
		{
			if (string.IsNullOrEmpty(connectionString))
			{
				var host = Environment.GetEnvironmentVariable("360VIDEO_DB_HOST");
				if (String.IsNullOrEmpty(host))
				{
					host = "localhost";
				}

				var user = Environment.GetEnvironmentVariable("360VIDEO_DB_USER");
				if (String.IsNullOrEmpty(user))
				{
					user = "postgres";
				}

				var password = Environment.GetEnvironmentVariable("360VIDEO_DB_PASSWORD");
				if (String.IsNullOrEmpty(password))
				{
					password = Environment.GetEnvironmentVariable("USER");
				}

				var database = "360video";

				connectionString = $"Server={host};Port=5432;Database={database};User Id={user};Password={password}";
			}

			return connectionString;
		}

		private static bool MatchPath(PathString fullPath, PathString startSegment)
		{
			return fullPath.StartsWithSegments(startSegment, StringComparison.OrdinalIgnoreCase);
		}

		private static string NewSessionToken(int numBytes)
		{
			var bytes = new byte[numBytes];
			rng.GetBytes(bytes);
			return Convert.ToBase64String(bytes).Substring(0, 32);
		}

		private static async Task<int> GetUserId(string username, NpgsqlConnection connection)
		{
			int? id;
			try
			{
				id = await connection.QueryFirstOrDefaultAsync<int?>("select userid from users where username = @username", new { username });
			}
			catch
			{
				id = null;
			}
			return id ?? -1;
		}

		private static async Task<int> GetUserIdFromToken(string token, NpgsqlConnection connection)
		{
			int? id;
			try
			{
				id = await connection.QueryFirstOrDefaultAsync<int?>("select userid from sessions where token = @token", new { token });
			}
			catch
			{
				id = null;
			}

			return id ?? -1;
		}

		private static async Task<bool> UserExists(string username, NpgsqlConnection connection)
		{
			try
			{
				int count = await connection.QuerySingleAsync<int>("select count(*) from users where username=@username", new { username });
				return count > 0;
			}
			catch
			{
				return false;
			}
		}

		private static async Task<bool> AuthenticateUser(string username, string password, NpgsqlConnection connection)
		{
			string storedPassword;
			try
			{
				storedPassword = await connection.QuerySingleAsync<string>("select pass from users where username = @username", new { username });
			}
			catch
			{
				return false;
			}

			return BCrypt.Net.BCrypt.Verify(password, storedPassword);
		}

		private static async Task<bool> AuthenticateToken(string token, NpgsqlConnection connection)
		{
			DateTime validUntil;

			try
			{
				validUntil = await connection.QuerySingleAsync<DateTime>("select expiry from sessions where token = @token", new { token });
			}
			catch
			{
				return false;
			}

			if (DateTime.UtcNow > validUntil)
			{
				await connection.ExecuteAsync("delete from sessions where token = @token", new { token });
				return false;
			}
			else
			{
				var newExpiry = DateTime.UtcNow.AddMinutes(sessionLength);
				await connection.ExecuteAsync("update sessions set expiry = @newExpiry where token = @token", new { newExpiry, token });
				return true;
			}
		}

		private async Task<bool> UserOwnsVideo(Guid guid, int userId, NpgsqlConnection connection)
		{
			int count = 0;
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
			int count = 0;
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

		static ReadOnlySpan<char> GetNextMetaValue(ref ReadOnlySpan<char> text)
		{
			var start = text.IndexOf(':') + 1;
			var end = text.IndexOf('\n');
			var length = end - start - 1;

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


		private static async Task WriteFile(HttpContext context, string filename, string contentType, string responseFileName)
		{
			//TODO(Simon): Pooling of buffers?
			byte[] buffer = new byte[fileBufferSize];

			try
			{
				context.Response.ContentType = contentType;
				context.Response.Headers["Content-Disposition"] = "attachment; filename=" + responseFileName;
				context.Response.ContentLength = new FileInfo(filename).Length;

				using (var stream = File.OpenRead(filename))
				{
					int read;
					while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
					{
						await context.Response.Body.WriteAsync(buffer, 0, read);
					}
				}
			}
			catch (FileNotFoundException)
			{
				await Write404(context);
				return;
			}
			catch (Exception e)
			{
				await WriteError(context, "Something went wrong while reading this file", StatusCodes.Status500InternalServerError, e);
				return;
			}
		}

		private static async Task WriteError(HttpContext context, string error, int errorCode, Exception e = null)
		{
			context.Response.StatusCode = errorCode;
			await context.Response.WriteAsync($"{{\"error\": \"{error}\"}}");
			#if DEBUG
			if (e != null)
			{
				await context.Response.WriteAsync(Environment.NewLine);
				await context.Response.WriteAsync(e.ToString());
				Console.WriteLine(e.StackTrace);
			}
			#endif
		}

		private static async Task Write404(HttpContext context, string message = "File Not Found")
		{
			await WriteError(context, message, StatusCodes.Status404NotFound);
		}
	}
}
