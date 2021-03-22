using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Http;
using Npgsql;
using static VivistaServer.CommonController;

namespace VivistaServer
{
	public class UserController
	{
		private const int passwordResetExpiry = 1 * 60;
		private const int sessionExpiry = 1 * 24 * 60;
		private const int bcryptWorkFactor = 12;


		[Route("POST", "/api/register")]
		[Route("POST", "/api/v1/register")]
		private static async Task RegisterPost(HttpContext context)
		{
			if (context.Request.HasFormContentType)
			{
				var form = context.Request.Form;

				string username = form["username"].ToString().ToLowerInvariant().Trim();
				string password = form["password"].ToString();
				string email = form["email"].ToString().ToLowerInvariant().Trim();

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

				if (email.Length == 0)
				{
					await WriteError(context, "Email too short", StatusCodes.Status400BadRequest);
					return;
				}

				using var connection = new NpgsqlConnection(Database.GetPgsqlConfig());
				connection.Open();

				var userExists = await UserExists(email, connection);
				var hashedPassword = BCrypt.Net.BCrypt.HashPassword(password, bcryptWorkFactor);

				if (!userExists)
				{
					try
					{
						int success = await connection.ExecuteAsync("insert into users (username, email, pass) values (@username, @email, @hashedPassword)", new { username, email, hashedPassword });

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
					var expiry = DateTime.UtcNow.AddMinutes(sessionExpiry);
					var userid = await GetUserIdFromEmail(email, connection);

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
			else
			{
				await WriteError(context, "Request did not contain a form", StatusCodes.Status400BadRequest);
			}
		}

		[Route("POST", "/api/login")]
		[Route("POST", "/api/v1/login")]
		private static async Task LoginPost(HttpContext context)
		{
			using var connection = new NpgsqlConnection(Database.GetPgsqlConfig());
			connection.Open();

			var form = context.Request.Form;
			string email = form["email"].ToString().ToLowerInvariant().Trim();
			string password = form["password"];
			bool success = await AuthenticateUser(email, password, connection);

			if (success)
			{
				string token = NewSessionToken(32);
				var expiry = DateTime.UtcNow.AddMinutes(sessionExpiry);
				var userid = await GetUserIdFromEmail(email, connection);

				try
				{
					await connection.ExecuteAsync("delete from sessions where userid = @userId", new { userid });
					await connection.ExecuteAsync("insert into sessions (token, expiry, userid) values (@token, @expiry, @userId)", new { token, expiry, userid });
				}
				catch (Exception e)
				{
					await WriteError(context, "Something went wrong while processing this request", StatusCodes.Status500InternalServerError, e);
				}

				//TODO(Simon): Wrap in JSON. Look for other occurrences in file
				await context.Response.WriteAsync(token);
			}
			else
			{
				await WriteError(context, "{}", StatusCodes.Status401Unauthorized);
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

				}
			}
		}

		[Route("GET", "/reset_password_finish")]
		private static async Task ResetPasswordFinishGet(HttpContext context)
		{
			using var connection = new NpgsqlConnection(Database.GetPgsqlConfig());
			connection.Open();

			var args = context.Request.Query;
			string token = args["token"].ToString();

			//TODO(Simon): Show HTML. Put token in hidden form element
		}

		[Route("POST", "/reset_password_finish")]
		private static async Task ResetPasswordFinishPost(HttpContext context)
		{
			using var connection = new NpgsqlConnection(Database.GetPgsqlConfig());
			connection.Open();

			var form = context.Request.Form;
			string email = form["email"].ToString();
			string token = form["token"].ToString();
			string password = form["password"].ToString();
			string confirmPassword = form["confirm_password"].ToString();

			if (await AuthenticatePasswordResetToken(email, token, connection))
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


		private static string NewSessionToken(int numBytes)
		{
			var bytes = new byte[numBytes];
			rng.GetBytes(bytes);
			return Convert.ToBase64String(bytes).Substring(0, 32);
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

		private static async Task<string> CreatePasswordResetToken(string email, NpgsqlConnection connection)
		{
			string token = NewSessionToken(32);
			var expiry = DateTime.UtcNow.AddMinutes(passwordResetExpiry);

			int userid = await GetUserIdFromEmail(email, connection);

			if (userid != -1)
			{
				await connection.ExecuteAsync("insert into password_reset_tokens (token, expiry, userid) values (@token, @expiry, @userId)", new { token, expiry, userid });
				return token;
			}

			return null;
		}

		private static async Task<bool> AuthenticatePasswordResetToken(string email, string token, NpgsqlConnection connection)
		{
			DateTime validUntil;

			try
			{
				validUntil = await connection.QuerySingleAsync<DateTime>("select expiry from password_reset_tokens where email=@email and token=@token", new { email, token });
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
