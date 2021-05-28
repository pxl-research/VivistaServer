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

	class ResetPasswordModel
	{
		public string email;
		public string token;
		public string error;
	}

	//TODO(Simon): User roles, role permissions
	public class UserController
	{
		public const int minPassLength = 8;

		private const int passwordResetExpiryMins = 1 * 60;
		private const int bcryptWorkFactor = 12;

		[Route("GET", "/register")]
		private static async Task RegisterGet(HttpContext context)
		{
			SetHTMLContentType(context);

			await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\register.liquid", null));
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
				email = context.Request.Form["email"].ToString().NormalizeEmail(),
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
				await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\register.liquid", templateContext));
				return;
			}

			if (code != StatusCodes.Status200OK)
			{
				model.error = result;
				var templateContext = new TemplateContext(model);
				await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\register.liquid", templateContext));
			}
			else
			{
				UserSessions.SetSessionCookie(context, result);
				await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\registerSuccess.liquid", null));
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

			await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\login.liquid", null));
		}

		[Route("POST", "/login")]
		private static async Task LoginPost(HttpContext context)
		{
			SetHTMLContentType(context);

			string result;
			int code;

			var model = new LoginModel
			{
				email = context.Request.Form["email"].ToString().NormalizeEmail(),
			};

			try
			{
				(result, code) = await LoginWithForm(context);
			}
			catch (Exception e)
			{
				model.error = "An unknown error happened while logging in. Please try again later.";
				var templateContext = new TemplateContext(model);
				await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\login.liquid", templateContext));
				return;
			}

			if (code != StatusCodes.Status200OK)
			{
				model.error = result;
				var templateContext = new TemplateContext(model);
				await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\login.liquid", templateContext));
			}
			else
			{
				UserSessions.SetSessionCookie(context, result);
				context.Response.Redirect("/");
			}
		}

		[Route("GET", "/logout")]
		private static async Task LogoutGet(HttpContext context)
		{
			if (await UserSessions.GetLoggedInUser(context) != null)
			{
				using var connection = Database.OpenNewConnection();

				await UserSessions.InvalidateSession(context.Request.Cookies["session"], connection);
				context.Response.Cookies.Delete("session");

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
			string email = args["email"].ToString().NormalizeEmail();
			string token = args["token"].ToString();

			int userid = await UserIdFromEmail(email, connection);

			bool success = await VerifyEmail(userid, token, connection);

			if (success)
			{
				await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\verifyEmailSuccess.liquid", null));
			}
			else
			{
				await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\verifyEmailFailure.liquid", null));
			}
		}

		[Route("GET", "/reset_password")]
		private static async Task ResetPasswordStartGet(HttpContext context)
		{
			SetHTMLContentType(context);

			await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\resetPassword.liquid", null));
		}

		[Route("POST", "/reset_password")]
		private static async Task ResetPasswordStartPost(HttpContext context)
		{
			SetHTMLContentType(context);

			using var connection = Database.OpenNewConnection();

			var form = context.Request.Form;
			string email = form["email"].ToString().NormalizeEmail();

			var userExistsTask = UserExists(email, connection);

			await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\resetPasswordSuccess.liquid", null));

			if (await userExistsTask)
			{
				var token = await CreatePasswordResetToken(email, connection);
				await EmailClient.SendPasswordResetMail(email, token);
			}
		}

		[Route("GET", "/reset_password_finish")]
		private static async Task ResetPasswordFinishGet(HttpContext context)
		{
			SetHTMLContentType(context);

			var args = context.Request.Query;
			var model = new ResetPasswordModel
			{
				email = args["email"].ToString().NormalizeEmail(),
				token = args["token"].ToString(),
			};

			//TODO(Simon): Show HTML. Put token in hidden form element
			var templateContext = new TemplateContext(model);
			await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\resetPasswordFinish.liquid", templateContext));
		}

		[Route("POST", "/reset_password_finish")]
		private static async Task ResetPasswordFinishPost(HttpContext context)
		{
			SetHTMLContentType(context);
			using var connection = Database.OpenNewConnection();

			var form = context.Request.Form;
			var model = new ResetPasswordModel
			{
				email = form["email"].ToString().NormalizeEmail(),
				token = form["token"].ToString(),
			};

			string password = form["password"].ToString();
			string confirmPassword = form["password-confirmation"].ToString();
			int userid = await UserIdFromEmail(model.email, connection);

			if (await AuthenticatePasswordResetToken(userid, model.token, connection))
			{
				if (password != confirmPassword)
				{
					model.error = "Passwords do not match";
					var templateContext = new TemplateContext(model);
					await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\resetPasswordFinish.liquid", templateContext));
					return;
				}

				if (password.Length < minPassLength)
				{
					model.error = "Password too short";
					var templateContext = new TemplateContext(model);
					await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\resetPasswordFinish.liquid", templateContext));
					return;
				}

				if (await UpdatePassword(model.email, password, connection))
				{
					await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\resetPasswordFinishSuccess.liquid", null));
					await DeletePasswordResetToken(userid, model.token, connection);
				}
				else
				{
					model.error = "An unknown error happened while resetting this password. Please try again later.";
					var templateContext = new TemplateContext(model);
					await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\resetPasswordFinish.liquid", templateContext));
				}
			}
			else
			{
				//NOTE(Simon): Do not tell exact reason, could be an attack vector
				model.error = "This password reset token is not valid.";
				var templateContext = new TemplateContext(model);
				await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\resetPasswordFinish.liquid", templateContext));
			}
		}




		//NOTE(Simon): If return int == 200, string == session token. If return int != 200, string == error description
		private static async Task<(string, int)> RegisterWithForm(HttpContext context)
		{
			if (context.Request.HasFormContentType)
			{
				var form = context.Request.Form;

				string username = form["username"].ToString().Trim();
				string password = form["password"].ToString();
				string email = form["email"].ToString().NormalizeEmail();

				if (username.Length < 3)
				{
					return ("Username too short", StatusCodes.Status400BadRequest);
				}

				if (password.Length < minPassLength)
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
					string verificationToken = Tokens.NewVerifyEmailToken();
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
					var userid = await UserIdFromEmail(email, connection);
					var token = await UserSessions.CreateNewSession(userid, connection);

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
			string email = form["email"].ToString().NormalizeEmail();
			string password = form["password"];
			bool success = await AuthenticateUser(email, password, connection);

			if (success)
			{
				var userid = await UserIdFromEmail(email, connection);
				var token = await UserSessions.CreateNewSession(userid, connection);

				//TODO(Simon): Wrap in JSON. Look for other occurrences in file
				return (token, StatusCodes.Status200OK);
			}
			else
			{
				return ("This combination of email and password was not found", StatusCodes.Status401Unauthorized);
			}
		}

		public static async Task<User> UserFromId(int userid, NpgsqlConnection connection)
		{
			return await connection.QuerySingleAsync<User>("select userid, username from users where userid=@userid", new { userid });
		}

		public static async Task<User> UserFromUsername(string username, NpgsqlConnection connection)
		{
			return await connection.QuerySingleAsync<User>("select userid, username from users where username=@username", new { username });
		}


		private static async Task<int> UserIdFromEmail(string email, NpgsqlConnection connection)
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

		public static async Task<int> UserIdFromUsername(string username, NpgsqlConnection connection)
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
			string token = Tokens.NewPasswordResetToken();
			var expiry = DateTime.UtcNow.AddMinutes(passwordResetExpiryMins);

			int userid = await UserIdFromEmail(email, connection);

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

		private static async Task<bool> AuthenticatePasswordResetToken(int userid, string token, NpgsqlConnection connection)
		{
			DateTime validUntil;

			try
			{
				validUntil = await connection.QuerySingleOrDefaultAsync<DateTime>("select expiry from password_reset_tokens where userid=@userid and token=@token", new { userid, token });
			}
			catch
			{
				return false;
			}

			return validUntil > DateTime.UtcNow;
		}

		private static async Task<bool> DeletePasswordResetToken(int userid, string token, NpgsqlConnection connection)
		{
			int result;
			try
			{
				result = await connection.ExecuteAsync("delete from password_reset_tokens where userid=@userid and token=@token", new { userid, token });
			}
			catch
			{
				return false;
			}

			return result > 0;
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
	}
}
