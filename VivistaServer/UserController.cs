using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Dapper;
using Fluid;
using Microsoft.AspNetCore.Http;
using Npgsql;
using static VivistaServer.CommonController;

namespace VivistaServer
{
	class RegisterModel
	{
		public string username;
		public string email;
		public string error;
	}

	class LoginModel
	{
		public string email;
		public string error;
	}

	//TODO(Simon): User roles, role permissions
	public class UserController
	{
		private const int passwordResetExpiry = 1 * 60;
		private const int sessionExpiry = 1 * 24 * 60;
		private const int bcryptWorkFactor = 12;

		[Route("GET", "/register")]
		private static async Task RegisterGet(HttpContext context)
		{
			SetHTMLContentType(context);

			await context.Response.WriteAsync(await HTMLRenderer.Render("Templates\\register.liquid", null));
		}

		[Route("POST", "/register")]
		private static async Task RegisterPost(HttpContext context)
		{
			SetHTMLContentType(context);

			string result;
			int code;

			var model = new RegisterModel
			{
				username = context.Request.Form["username"].ToString(),
				email = context.Request.Form["email"].ToString(),
				error = ""
			};

			try
			{
				(result, code) = await RegisterWithForm(context);
			}
			catch (Exception e)
			{
				model.error = "An unknown error happened while registering this account. Please try again later.";
				var templateContext = new TemplateContext(model);
				await context.Response.WriteAsync(await HTMLRenderer.Render("Templates\\register.liquid", templateContext));
				return;
			}

			if (code != StatusCodes.Status200OK)
			{
				model.error = result;
				var templateContext = new TemplateContext(model);
				await context.Response.WriteAsync(await HTMLRenderer.Render("Templates\\register.liquid", templateContext));
			}
			else
			{
				var cookies = context.Response.Cookies;
				cookies.Append("session", result);
				await context.Response.WriteAsync(await HTMLRenderer.Render("Templates\\registerSuccess.liquid", null));
			}
		}

		[Route("POST", "/api/register")]
		[Route("POST", "/api/v1/register")]
		private static async Task RegisterPostApi(HttpContext context)
		{
			string result;
			int code;

			try
			{
				(result, code) = await RegisterWithForm(context);
			}
			catch (Exception e)
			{
				await WriteError(context, "Something went wrong while processing this request", StatusCodes.Status500InternalServerError, e);
				return;
			}

			if (code != StatusCodes.Status200OK)
			{
				await WriteError(context, result, code);
			}
			else
			{
				await context.Response.Body.WriteAsync(Utf8Json.JsonSerializer.SerializeUnsafe(new { token = result }));
			}
		}

		[Route("GET", "/login")]
		private static async Task LoginGet(HttpContext context)
		{
			SetHTMLContentType(context);

			await context.Response.WriteAsync(await HTMLRenderer.Render("Templates\\login.liquid", null));
		}

		[Route("POST", "/login")]
		private static async Task LoginPost(HttpContext context)
		{
			SetHTMLContentType(context);

			string result;
			int code;

			var model = new LoginModel
			{
				email = context.Request.Form["email"].ToString(),
			};

			try
			{
				(result, code) = await LoginWithForm(context);
			}
			catch (Exception e)
			{
				model.error = "An unknown error happened while logging in. Please try again later.";
				var templateContext = new TemplateContext(model);
				await context.Response.WriteAsync(await HTMLRenderer.Render("Templates\\login.liquid", templateContext));
				return;
			}

			if (code != StatusCodes.Status200OK)
			{
				model.error = result;
				var templateContext = new TemplateContext(model);
				await context.Response.WriteAsync(await HTMLRenderer.Render("Templates\\login.liquid", templateContext));
			}
			else
			{
				var cookies = context.Response.Cookies;
				cookies.Append("session", result);

				context.Response.Redirect("/");
			}
		}

		[Route("POST", "/api/login")]
		[Route("POST", "/api/v1/login")]
		private static async Task LoginPostApi(HttpContext context)
		{
			string result;
			int code;

			try
			{
				(result, code) = await LoginWithForm(context);
			}
			catch (Exception e)
			{
				await WriteError(context, "Something went wrong while processing this request", StatusCodes.Status500InternalServerError, e);
				return;
			}

			if (code != StatusCodes.Status200OK)
			{
				await WriteError(context, "{}", StatusCodes.Status401Unauthorized);
			}
			else
			{
				await context.Response.Body.WriteAsync(Utf8Json.JsonSerializer.SerializeUnsafe(new { token = result }));
			}
		}

		[Route("GET", "/verify_email")]
		private static async Task VerifyEmailGet(HttpContext context)
		{
			SetHTMLContentType(context);
			using var connection = Database.OpenNewConnection();

			var args = context.Request.Query;
			string email = args["email"].ToString();
			string token = args["token"].ToString();

			int userid = await GetUserIdFromEmail(email, connection);

			bool success = await VerifyEmail(userid, token, connection);

			if (success)
			{

			}
			else
			{

			}
		}

		[Route("POST", "/api/reset_password_start")]
		[Route("POST", "/api/v1/reset_password_start")]
		private static async Task ResetPasswordStartPost(HttpContext context)
		{
			using var connection = new NpgsqlConnection(Database.GetPgsqlConfig());
			connection.Open();

			var form = context.Request.Form;
			string email = form["email"].ToString().ToLowerInvariant().Trim();

			var userExistsTask = UserExists(email, connection);

			//NOTE(Simon): Send response before sending the reset email, so that a timing attack trying to guess existing emails is not possible
			var writeTask = context.Response.WriteAsync("A password reset link has been sent to the provided email. It will remain valid for 1 hour.");

			bool userExists = await userExistsTask;
			await writeTask;

			if (userExists)
			{
				var token = await CreatePasswordResetToken(email, connection);

				//TODO(Simon): Send email
				if (token != null)
				{
					await EmailClient.SendPasswordResetMail(email, token);
				}
			}
		}

		[Route("GET", "/reset_password_finish")]
		private static async Task ResetPasswordFinishGet(HttpContext context)
		{
			SetHTMLContentType(context);
			using var connection = new NpgsqlConnection(Database.GetPgsqlConfig());
			connection.Open();

			var args = context.Request.Query;
			string token = args["token"].ToString();

			//TODO(Simon): Show HTML. Put token in hidden form element
			var model = new TemplateContext(new { test = "test" });
			await context.Response.WriteAsync(await HTMLRenderer.Render("testTemplate", model));
		}

		[Route("POST", "/reset_password_finish")]
		private static async Task ResetPasswordFinishPost(HttpContext context)
		{
			SetHTMLContentType(context);
			using var connection = Database.OpenNewConnection();

			var form = context.Request.Form;
			string email = form["email"].ToString();
			string token = form["token"].ToString();
			string password = form["password"].ToString();
			string confirmPassword = form["confirm_password"].ToString();
			int userid = await GetUserIdFromEmail(email, connection);

			if (await AuthenticatePasswordResetToken(userid, token, connection))
			{
				if (password != confirmPassword)
				{
					await WriteError(context, "Passwords do not match", StatusCodes.Status400BadRequest);
					return;
				}

				if (password.Length == 0)
				{
					await WriteError(context, "Password too short", StatusCodes.Status400BadRequest);
					return;
				}

				if (await UpdatePassword(email, password, connection))
				{
					await context.Response.WriteAsync("Password updated succesfully");
				}
				else
				{
					await WriteError(context, "Password update failed", StatusCodes.Status500InternalServerError);
				}
			}
			else
			{
				//NOTE(Simon): Do not tell exact reason, could be an attack vector
				await WriteError(context, "Reset token is invalid", StatusCodes.Status400BadRequest);
			}
		}

		//NOTE(Simon): If return int == 200, string == session token. If return int != 200, string == error description
		private static async Task<(string, int)> RegisterWithForm(HttpContext context)
		{
			if (context.Request.HasFormContentType)
			{
				var form = context.Request.Form;

				string username = form["username"].ToString().ToLowerInvariant().Trim();
				string password = form["password"].ToString();
				string email = form["email"].ToString().ToLowerInvariant().Trim();

				if (username.Length < 3)
				{
					return ("Username too short", StatusCodes.Status400BadRequest);
				}

				if (password.Length < 8)
				{
					return ("Password too short", StatusCodes.Status400BadRequest);
				}

				//NOTE(Simon): Shortest possible is a@a.a
				if (email.Length < 5)
				{
					return ("Email too short", StatusCodes.Status400BadRequest);
				}

				using var connection = Database.OpenNewConnection();

				var userExists = await UserExists(email, connection);
				var hashedPassword = BCrypt.Net.BCrypt.HashPassword(password, bcryptWorkFactor);

				if (!userExists)
				{
					string verificationToken = NewVerifyEmailToken();
					int success = await connection.ExecuteAsync(@"insert into users (username, email, pass, verification_token)
																	values (@username, @email, @hashedPassword, @verificationToken)",
																new { username, email, hashedPassword, verificationToken });

					if (success == 0)
					{
						throw new Exception("Something went wrong while writing new user to db");
					}

					await EmailClient.SendEmailConfirmationMail(email, verificationToken);
				}
				else
				{
					return ("This user already exists", StatusCodes.Status409Conflict);
				}

				//NOTE(Simon): Create session token to immediately log user in.
				{
					var token = await CreateNewSession(email, connection);

					return (token, StatusCodes.Status200OK);
				}
			}
			else
			{
				return ("Request did not contain a form", StatusCodes.Status400BadRequest);
			}
		}

		//NOTE(Simon): If return int == 200, string == session token. If return int != 200, string == error description
		private static async Task<(string, int)> LoginWithForm(HttpContext context)
		{
			using var connection = Database.OpenNewConnection();

			var form = context.Request.Form;
			string email = form["email"].ToString().ToLowerInvariant().Trim();
			string password = form["password"];
			bool success = await AuthenticateUser(email, password, connection);

			if (success)
			{
				string token = NewToken(32);
				var expiry = DateTime.UtcNow.AddMinutes(sessionExpiry);
				var userid = await GetUserIdFromEmail(email, connection);

				await connection.ExecuteAsync("delete from sessions where userid = @userId", new { userid });
				await connection.ExecuteAsync("insert into sessions (token, expiry, userid) values (@token, @expiry, @userId)", new { token, expiry, userid });

				//TODO(Simon): Wrap in JSON. Look for other occurrences in file
				return (token, StatusCodes.Status200OK);
			}
			else
			{
				return ("This combination of email and password was not found", StatusCodes.Status401Unauthorized);
			}
		}

		private static async Task<int> GetUserIdFromEmail(string email, NpgsqlConnection connection)
		{
			int? id;
			try
			{
				id = await connection.QueryFirstOrDefaultAsync<int?>("select userid from users where email = @email", new { email });
			}
			catch
			{
				id = null;
			}
			return id ?? -1;
		}

		public static async Task<int> GetUserIdFromUsername(string username, NpgsqlConnection connection)
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

		public static async Task<int> GetUserIdFromToken(string token, NpgsqlConnection connection)
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

		private static async Task<bool> UserExists(string email, NpgsqlConnection connection)
		{
			try
			{
				int count = await connection.QuerySingleAsync<int>("select count(*) from users where email=@email", new { email });
				return count > 0;
			}
			catch
			{
				return false;
			}
		}

		private static async Task<bool> UpdatePassword(string email, string password, NpgsqlConnection connection)
		{
			string hashedPassword = BCrypt.Net.BCrypt.HashPassword(password, bcryptWorkFactor);
			int rows;

			try
			{
				rows = await connection.ExecuteAsync("update users set pass = @hashedPassword where email = @email", new {email, hashedPassword});
			}
			catch
			{
				return false;
			}

			return rows > 0;
		}

		private static string NewToken(int numBytes)
		{
			var bytes = new byte[numBytes];
			rng.GetBytes(bytes);
			return Convert.ToBase64String(bytes).Substring(0, numBytes);
		}

		private static string NewSessionToken()
		{
			return NewToken(32);
		}

		private static string NewVerifyEmailToken()
		{
			return NewToken(16);
		}

		private static async Task<bool> VerifyEmail(int userid, string token, NpgsqlConnection connection)
		{
			bool valid;
			try
			{
				int count = await connection.QuerySingleAsync<int>("select count(*) from users where userid=@userid and verification_token=@token", new { userid, token });

				valid = count > 0;
			}
			catch
			{
				return false;
			}

			return valid;
		}

		private static async Task<string> CreatePasswordResetToken(string email, NpgsqlConnection connection)
		{
			string token = NewToken(32);
			var expiry = DateTime.UtcNow.AddMinutes(passwordResetExpiry);

			int userid = await GetUserIdFromEmail(email, connection);

			if (userid != -1)
			{
				await connection.ExecuteAsync(@"insert into password_reset_tokens (token, expiry, userid) 
											values (@token, @expiry, @userId)
											on conflict(userid)
											do update set token=@token, expiry=@expiry", new { token, expiry, userid });
				return token;
			}

			return null;
		}

		private static async Task<string> CreateNewSession(string email, NpgsqlConnection connection)
		{
			var token = NewSessionToken();
			var expiry = DateTime.UtcNow.AddMinutes(sessionExpiry);
			var userid = await GetUserIdFromEmail(email, connection);

			if (userid == -1)
			{
				throw new Exception("Something went wrong while retrieving UserID");
			}

			await connection.ExecuteAsync("insert into sessions (token, expiry, userid) values (@token, @expiry, @userid)", new { token, expiry, userid });

			return token;
		}

		private static async Task<bool> AuthenticatePasswordResetToken(int userid, string token, NpgsqlConnection connection)
		{
			DateTime validUntil;

			try
			{
				validUntil = await connection.QuerySingleAsync<DateTime>("select expiry from password_reset_tokens where userid=@userid and token=@token", new { userid, token });
			}
			catch
			{
				return false;
			}

			return validUntil < DateTime.UtcNow;
		}

		private static async Task<bool> AuthenticateUser(string email, string password, NpgsqlConnection connection)
		{
			string storedPassword;
			try
			{
				storedPassword = await connection.QuerySingleAsync<string>("select pass from users where email = @email", new { email });
			}
			catch
			{
				return false;
			}

			return BCrypt.Net.BCrypt.Verify(password, storedPassword);
		}

		public static async Task<bool> AuthenticateToken(string token, NpgsqlConnection connection)
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
				var newExpiry = DateTime.UtcNow.AddMinutes(sessionExpiry);
				await connection.ExecuteAsync("update sessions set expiry = @newExpiry where token = @token", new { newExpiry, token });
				return true;
			}
		}
	}
}
