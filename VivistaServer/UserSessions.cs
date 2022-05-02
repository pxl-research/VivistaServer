using System;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Http;
using System.Runtime.Caching;
using Npgsql;
using System.Collections;
using System.Collections.Generic;

namespace VivistaServer
{
	public class Session
	{
		public int userid;
		public DateTime expiry;
		public bool IsValid => expiry > DateTime.UtcNow;

		public static Session noSession = new Session { userid = -1, expiry = DateTime.MinValue };
	}

	public struct Role
	{
		public int id;
		public string name { get; set; }
	}

	public class User
	{
		public int userid;
		public string username;
		public string email;
		public string pictureId;
		private List<int> roles;


		public async Task<List<int>> GetRoles(NpgsqlConnection connection, HttpContext context)
		{
			if(roles == null)
			{
				roles = (List<int>)await Database.QueryAsync<int>(connection, "SELECT roleid FROM user_roles WHERE userid = @userid", context,new { userid = userid });
			}
			return roles;
		}

		public static async Task<bool> IsUserAdmin(HttpContext context, NpgsqlConnection connection)
		{
			return await IsUserSpecificRole(context, "admin", connection);
		}

		public static async Task<bool> IsUserSpecificRole(HttpContext context, string role, NpgsqlConnection connection)
		{
			var user = await UserSessions.GetLoggedInUser(context);
			if (user != null)
			{
				var roles = await user.GetRoles(connection, context);
				var adminId = RoleController.GetRoleId(role);
				if (roles.Contains(adminId))
				{
					return true;
				}
			}
			return false;
		}
	}

	public class UserSessions
	{
		public static long activeSessions => cache.GetCount();

		private const int sessionExpiryMins = 30 * 24 * 60;
		private static MemoryCache cache = MemoryCache.Default;
		private static CacheItemPolicy defaultPolicy = new CacheItemPolicy()
		{
			SlidingExpiration = TimeSpan.FromMinutes(20)
		};

		public static int GetItemsInUserCache()
		{
			return (int)cache.GetCount();
		}

		public static async Task<User> GetLoggedInUser(HttpContext context)
		{
			CommonController.LogDebug("Begin searching for logged in user");

			string sessionToken = context.Request.Cookies["session"];
			if (String.IsNullOrEmpty(sessionToken))
			{
				CommonController.LogDebug("No session cookie");
				sessionToken = context.Request.Query["token"].ToString();
				if (String.IsNullOrEmpty(sessionToken))
				{
					CommonController.LogDebug("No session in query either. No user found");
					return null;
				}
			}

			if (cache[sessionToken] != null)
			{
				CommonController.LogDebug("Session in cache");
				return (User)cache[sessionToken];
			}
			else
			{
				CommonController.LogDebug("Session not in cache. Retrieving from db");
				var user = await GetLoggedInUserSkipCache(sessionToken, context);
				if (user == null)
				{
					CommonController.LogDebug("Bad token. No user found.");
					return null;
				}
				else
				{
					cache.Set(sessionToken, user, defaultPolicy);
					return user;
				}
			}
		}

		public static async Task<User> GetLoggedInUserSkipCache(string sessionToken, HttpContext context)
		{
			using var connection = Database.OpenNewConnection();
			var session = await AuthenticateWithToken(sessionToken, connection, context);
			if (session.IsValid)
			{
				return await Database.QuerySingleAsync<User>(connection,"select userid, username, email, pictureid from users where userid=@userid", context, new { session.userid });
			}
			else
			{
				return null;
			}
		}

		private static async Task<Session> AuthenticateWithToken(string token, NpgsqlConnection connection, HttpContext context)
		{
			Session session;

			try
			{
				session = await Database.QuerySingleAsync<Session>(connection,"select expiry, userid from sessions where token = @token", context, new {token});
			}
			catch
			{
				return Session.noSession;
			}

			if (!session.IsValid)
			{
				await InvalidateSession(token, connection, context);
				return session;
			}
			else
			{
				var newExpiry = DateTime.UtcNow.AddMinutes(sessionExpiryMins);
				await Database.ExecuteAsync(connection,"update sessions set expiry = @newExpiry where token = @token",context, new { newExpiry, token });
				return session;
			}
		}

		public static async Task InvalidateSession(string token, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				cache.Remove(token);
				await Database.ExecuteAsync(connection,"delete from sessions where token=@token", context, new {token});
			}
			catch
			{
				return;
			}
		}

		public static async Task<string> CreateNewSession(int userid, NpgsqlConnection connection, HttpContext context)
		{
			var token = Tokens.NewSessionToken();
			var expiry = DateTime.UtcNow.AddMinutes(sessionExpiryMins);

			if (userid == -1)
			{
				throw new Exception("Something went wrong while retrieving UserID");
			}

			await Database.ExecuteAsync(connection,"insert into sessions (token, expiry, userid) values (@token, @expiry, @userid)", context,new { token, expiry, userid });

			return token;
		}
		public static void SetSessionCookie(HttpContext context, string token)
		{
			var cookies = context.Response.Cookies;
			cookies.Append("session", token, new CookieOptions { MaxAge = TimeSpan.FromMinutes(sessionExpiryMins) });
		}
	}
}