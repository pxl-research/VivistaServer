using System;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Http;
using System.Runtime.Caching;
using Npgsql;

namespace VivistaServer
{
	public class Session
	{
		public int userid;
		public DateTime expiry;
		public bool IsValid => expiry > DateTime.UtcNow;

		public static Session noSession = new Session { userid = -1, expiry = DateTime.MinValue };
	}

	public class User
	{
		public int userid;
		public string username;
		public string email;
	}

	public class UserSessions
	{
		private const int sessionExpiryMins = 30 * 24 * 60;
		private static MemoryCache cache = MemoryCache.Default;
		private static CacheItemPolicy defaultPolicy = new CacheItemPolicy()
		{
			SlidingExpiration = TimeSpan.FromMinutes(20)
		};

		public static async Task<User> GetLoggedInUser(HttpContext context)
		{
			string sessionToken = context.Request.Cookies["session"];
			if (String.IsNullOrEmpty(sessionToken))
			{
				sessionToken = context.Request.Query["token"].ToString();
				if (String.IsNullOrEmpty(sessionToken))
				{
					return null;
				}
			}

			if (cache[sessionToken] != null)
			{
				return (User)cache[sessionToken];
			}
			else
			{
				var user = await GetLoggedInUserFromDb(sessionToken);
				cache.Set(sessionToken, user, defaultPolicy);
				return user;
			}
		}

		public static async Task<User> GetLoggedInUserFromDb(string sessionToken)
		{
			var connection = Database.OpenNewConnection();
			var session = await AuthenticateWithToken(sessionToken, connection);
			if (session.IsValid)
			{
				return await connection.QuerySingleAsync<User>("select userid, username, email from users where userid=@userid", new { session.userid });
			}
			else
			{
				return null;
			}
		}

		private static async Task<Session> AuthenticateWithToken(string token, NpgsqlConnection connection)
		{
			Session session;

			try
			{
				session = await connection.QuerySingleAsync<Session>("select expiry, userid from sessions where token = @token", new {token});
			}
			catch
			{
				return Session.noSession;
			}

			if (!session.IsValid)
			{
				await InvalidateSession(token, connection);
				return session;
			}
			else
			{
				var newExpiry = DateTime.UtcNow.AddMinutes(sessionExpiryMins);
				await connection.ExecuteAsync("update sessions set expiry = @newExpiry where token = @token", new { newExpiry, token });
				return session;
			}
		}

		public static async Task InvalidateSession(string token, NpgsqlConnection connection)
		{
			try
			{
				cache.Remove(token);
				await connection.ExecuteAsync("delete from sessions where token=@token", new {token});
			}
			catch
			{
				return;
			}
		}

		public static async Task<string> CreateNewSession(int userid, NpgsqlConnection connection)
		{
			var token = Tokens.NewSessionToken();
			var expiry = DateTime.UtcNow.AddMinutes(sessionExpiryMins);

			if (userid == -1)
			{
				throw new Exception("Something went wrong while retrieving UserID");
			}

			await connection.ExecuteAsync("insert into sessions (token, expiry, userid) values (@token, @expiry, @userid)", new { token, expiry, userid });

			return token;
		}

		public static void SetSessionCookie(HttpContext context, string token)
		{
			var cookies = context.Response.Cookies;
			cookies.Append("session", token, new CookieOptions { MaxAge = TimeSpan.FromMinutes(sessionExpiryMins) });
		}
	}
}