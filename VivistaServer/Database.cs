using System;
using Npgsql;

namespace VivistaServer
{
	public class Database
	{
		public static NpgsqlConnection OpenNewConnection()
		{
			var conn = new NpgsqlConnection(GetPgsqlConfig());
			conn.Open();
			return conn;
		}

		//NOTE(Simon): Use GetPgsqlConfig() instead of this directly, it handles caching of this variable.
		private static string connectionString;

		private static string GetPgsqlConfig()
		{
			if (string.IsNullOrEmpty(connectionString))
			{
				var host = Environment.GetEnvironmentVariable("VIVISTA_DB_HOST");
				if (String.IsNullOrEmpty(host))
				{
					host = "localhost";
				}

				var user = Environment.GetEnvironmentVariable("VIVISTA_DB_USER");
				if (String.IsNullOrEmpty(user))
				{
					user = "postgres";
				}

				var password = Environment.GetEnvironmentVariable("VIVISTA_DB_PASSWORD");
				if (String.IsNullOrEmpty(password))
				{
					password = Environment.GetEnvironmentVariable("USER");
				}

				var database = "postgres";

				connectionString = $"Server={host};Port=5432;Database={database};User Id={user};Password={password};Pooling=true;Minimum Pool Size=0;Maximum Pool Size=100;";
			}

			return connectionString;
		}

	}
}