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
			bool success;

			var model = new RegisterModel
			{
				username = context.Request.Form["username"].ToString(),
				email = context.Request.Form["email"].ToString().NormalizeEmail(),
				error = ""
			};

			try
			{
				(success, result) = await RegisterWithForm(context);
			}
			catch (Exception e)
			{
				model.error = "An unknown error happened while registering this account. Please try again later.";
				var templateContext = new TemplateContext(model);
				await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\register.liquid", templateContext));
				return;
			}

			if (!success)
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
			bool success;

			try
			{
				(success, result) = await RegisterWithForm(context);
			}
			catch (Exception e)
			{
				await WriteError(context, "Something went wrong while processing this request", StatusCodes.Status500InternalServerError, e);
				return;
			}

			if (!success)
			{
				await WriteError(context, result, StatusCodes.Status400BadRequest);
			}
			else
			{
				await context.Response.Body.WriteAsync(Utf8Json.JsonSerializer.SerializeUnsafe(new { session = result }));
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
			bool success;

			var model = new LoginModel
			{
				email = context.Request.Form["email"].ToString().NormalizeEmail(),
			};

			try
			{
				(success, result) = await LoginWithForm(context);
			}
			catch (Exception e)
			{
				model.error = "An unknown error happened while logging in. Please try again later.";
				var templateContext = new TemplateContext(model);
				await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\login.liquid", templateContext));
				return;
			}

			if (!success)
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

				await UserSessions.InvalidateSession(context.Request.Cookies["session"], connection, context);
				context.Response.Cookies.Delete("session");

				context.Response.Redirect("/");
			}
		}

		[Route("POST", "/api/login")]
		[Route("POST", "/api/v1/login")]
		private static async Task LoginPostApi(HttpContext context)
		{
			string result;
			bool success;

			try
			{
				(success, result) = await LoginWithForm(context);
			}
			catch (Exception e)
			{
				await WriteError(context, "Something went wrong while processing this request", StatusCodes.Status500InternalServerError, e);
				return;
			}

			if (!success)
			{
				await WriteError(context, result, StatusCodes.Status401Unauthorized);
			}
			else
			{
				await context.Response.Body.WriteAsync(Utf8Json.JsonSerializer.SerializeUnsafe(new { session = result }));
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

			int userid = await UserIdFromEmail(email, connection, context);

			bool success = await VerifyEmail(userid, token, connection, context);

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

			var userExistsTask = UserExists(email, connection, context);

			await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\resetPasswordSuccess.liquid", null));

			if (await userExistsTask)
			{
				var token = await CreatePasswordResetToken(email, connection, context);
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
			int userid = await UserIdFromEmail(model.email, connection, context);

			if (await AuthenticatePasswordResetToken(userid, model.token, connection, context))
			{
				var (success, result) = ValidatePassword(password, confirmPassword);
				if (!success)
				{
					model.error = result;
					var templateContext = new TemplateContext(model);
					await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\resetPasswordFinish.liquid", templateContext));
					return;
				}

				if (await UpdatePassword(model.email, password, connection, context))
				{
					await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\resetPasswordFinishSuccess.liquid", null));
					await DeletePasswordResetToken(userid, model.token, connection, context);
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

		[Route("GET", "/settings")]
		private static async Task SettingsGet(HttpContext context)
		{
			SetHTMLContentType(context);

			await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\settings.liquid", null));
		}

		[Route("POST", "/update_password")]
		private static async Task UpdatePasswordPost(HttpContext context)
		{
			SetHTMLContentType(context);

			string currentPassword = context.Request.Form["current-password"];
			string password = context.Request.Form["new-password"];
			string passwordConfirmation = context.Request.Form["password-confirmation"];

			var (success, result) = ValidatePassword(password, passwordConfirmation);

			if (!success)
			{
				var templateContext = new TemplateContext(new { success = false, message = result });
				await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\settings.liquid", templateContext));
			}
			else
			{
				using var connection = Database.OpenNewConnection();
				var user = await UserSessions.GetLoggedInUser(context);
				var currentPassCorrect = await AuthenticateUser(user.email, currentPassword, connection, context);

				if (currentPassCorrect)
				{
					if (await UpdatePassword(user.email, password, connection, context))
					{
						var templateContext = new TemplateContext(new {success = true, message = ""});
						await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\settings.liquid", templateContext));
					}
					else
					{
						var templateContext = new TemplateContext(new { success = false, message = "Something went wrong while updating password. Please try again later" });
						await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\settings.liquid", templateContext));
					}
				}
				else
				{
					var templateContext = new TemplateContext(new { success = false, message = "Current password is wrong" });
					await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\settings.liquid", templateContext));
				}
			}
		}



		private static async Task<(bool success, string result)> RegisterWithForm(HttpContext context)
		{
			if (context.Request.HasFormContentType)
			{
				var form = context.Request.Form;

				string username = form["username"].ToString().Trim();
				string password = form["password"].ToString();
				string passwordConfirmation = form["password-confirmation"].ToString();
				string email = form["email"].ToString().NormalizeEmail();

				if (username.Length < 3)
				{
					return (false, "Username too short");
				}

				var (success, result) = ValidatePassword(password, passwordConfirmation);
				if (!success)
				{
					return (false, result);
				}

				//NOTE(Simon): Shortest possible is a@a.a
				if (email.Length < 5)
				{
					return (false, "Email too short");
				}

				using var connection = Database.OpenNewConnection();

				bool userExists = await UserExists(email, connection, context);
				string hashedPassword = BCrypt.Net.BCrypt.HashPassword(password, bcryptWorkFactor);

				if (!userExists)
				{
					string verificationToken = Tokens.NewVerifyEmailToken();
					int querySuccess = await Database.ExecuteAsync(connection,@"insert into users (username, email, pass, verification_token)
																	values (@username, @email, @hashedPassword, @verificationToken)", 
                                                                    context,
																new { username, email, hashedPassword, verificationToken });

					if (querySuccess == 0)
					{
						throw new Exception("Something went wrong while writing new user to db");
					}

					EmailClient.SendEmailConfirmationMail(email, verificationToken);
				}
				else
				{
					return (false, "This user already exists");
				}

				//NOTE(Simon): Create session token to immediately log user in.
				{
					int userid = await UserIdFromEmail(email, connection, context);
					string token = await UserSessions.CreateNewSession(userid, connection, context);

					return (true, token);
				}
			}
			else
			{
				return (false, "Request did not contain a form");
			}
		}

		private static async Task<(bool success, string result)> LoginWithForm(HttpContext context)
		{
			using var connection = Database.OpenNewConnection();

			var form = context.Request.Form;
			string email = form["email"].ToString().NormalizeEmail();
			string password = form["password"];
			bool success = await AuthenticateUser(email, password, connection, context);

			if (success)
			{
				var userid = await UserIdFromEmail(email, connection, context);
				var token = await UserSessions.CreateNewSession(userid, connection, context);

				return (true, token);
			}
			else
			{
				return (false, "This combination of email and password was not found");
			}
		}

		private static (bool success, string result) ValidatePassword(string password, string confirmation)
		{
			if (password != confirmation)
			{
				return (false, "Passwords do not match");
			}

			if (string.IsNullOrEmpty(password) || password.Length < minPassLength)
			{
				return (false, $"Password should be at least {minPassLength} characters long");
			}

			return (true, "");
		}

		public static async Task<User> UserFromId(int userid, NpgsqlConnection connection, HttpContext context)
		{
			return await Database.QuerySingleAsync<User>(connection, "select userid, username from users where userid=@userid", context, new { userid });
		}

		public static async Task<User> UserFromUsername(string username, NpgsqlConnection connection, HttpContext context)
		{
			return await Database.QuerySingleAsync<User>(connection, "select userid, username from users where username=@username", context,new { username });
		}


		private static async Task<int> UserIdFromEmail(string email, NpgsqlConnection connection, HttpContext context)
		{
			int? id;
			try
			{
				id = await Database.QueryFirstOrDefaultAsync<int?>(connection,"select userid from users where email = @email", context, new { email });
			}
			catch
			{
				id = null;
			}
			return id ?? -1;
		}

		public static async Task<int> UserIdFromUsername(string username, NpgsqlConnection connection, HttpContext context)
		{
			int? id;
			try
			{
				id = await Database.QueryFirstOrDefaultAsync<int?>(connection, "select userid from users where username = @username", context, new { username });
			}
			catch
			{
				id = null;
			}
			return id ?? -1;
		}

		private static async Task<bool> UserExists(string email, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				int count = await Database.QuerySingleAsync<int>(connection, "select count(*) from users where email=@email", context, new { email });
				return count > 0;
			}
			catch
			{
				return false;
			}
		}

		private static async Task<bool> UpdatePassword(string email, string password, NpgsqlConnection connection, HttpContext context)
		{
			string hashedPassword = BCrypt.Net.BCrypt.HashPassword(password, bcryptWorkFactor);
			int rows;

			try
			{
				rows = await Database.ExecuteAsync(connection, "update users set pass = @hashedPassword where email = @email", context,new { email, hashedPassword });
			}
			catch
			{
				return false;
			}

			return rows > 0;
		}

		private static async Task<bool> VerifyEmail(int userid, string token, NpgsqlConnection connection, HttpContext context)
		{
			bool valid;
			try
			{
				int count = await Database.QuerySingleAsync<int>(connection, "select count(*) from users where userid=@userid and verification_token=@token", context, new { userid, token });

				valid = count > 0;
			}
			catch
			{
				return false;
			}

			return valid;
		}

		private static async Task<string> CreatePasswordResetToken(string email, NpgsqlConnection connection, HttpContext context)
		{
			string token = Tokens.NewPasswordResetToken();
			var expiry = DateTime.UtcNow.AddMinutes(passwordResetExpiryMins);

			int userid = await UserIdFromEmail(email, connection, context);

			if (userid != -1)
			{
				await Database.ExecuteAsync(connection,@"insert into password_reset_tokens (token, expiry, userid) 
											values (@token, @expiry, @userId)
											on conflict(userid)
											do update set token=@token, expiry=@expiry", context,new { token, expiry, userid });
				return token;
			}

			return null;
		}

		private static async Task<bool> AuthenticatePasswordResetToken(int userid, string token, NpgsqlConnection connection, HttpContext context)
		{
			DateTime validUntil;

			try
			{
				validUntil = await Database.QuerySingleOrDefaultAsync<DateTime>(connection, "select expiry from password_reset_tokens where userid=@userid and token=@token", context, new { userid, token });
			}
			catch
			{
				return false;
			}

			return validUntil > DateTime.UtcNow;
		}

		private static async Task<bool> DeletePasswordResetToken(int userid, string token, NpgsqlConnection connection, HttpContext context)
		{
			int result;
			try
			{
				result = await Database.ExecuteAsync(connection, "delete from password_reset_tokens where userid=@userid and token=@token", context, new { userid, token });
			}
			catch
			{
				return false;
			}

			return result > 0;
		}

		private static async Task<bool> AuthenticateUser(string email, string password, NpgsqlConnection connection, HttpContext context)
		{
			string storedPassword;
			try
			{
				storedPassword = await Database.QuerySingleAsync<string>(connection,"select pass from users where email = @email", context, new { email });
			}
			catch
			{
				return false;
			}

			return BCrypt.Net.BCrypt.Verify(password, storedPassword);
		}
	}
}
