using System;
using Npgsql;

namespace VivistaServer
{
	public class Database
	{
		public static NpgsqlConnection OpenNewConnection()
		{
			CommonController.LogDebug("Opening new SQL connection");
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
					Console.WriteLine("No host defined in env var. Using default 'localhost'");
					host = "localhost";
				}

				var user = Environment.GetEnvironmentVariable("VIVISTA_DB_USER");
				if (String.IsNullOrEmpty(user))
				{
					Console.WriteLine("No user defined in env var. Using default 'postgres'");
					user = "postgres";
				}

				var password = Environment.GetEnvironmentVariable("VIVISTA_DB_PASSWORD");
				if (String.IsNullOrEmpty(password))
				{
					Console.WriteLine("No password defined in env var");
				}

				var database = Environment.GetEnvironmentVariable("VIVISTA_DB_DATABASE");
				if (String.IsNullOrEmpty(database))
				{
					database = "postgres";
					Console.WriteLine("No database defined in env var. Using default 'postgres'");
				}


				string parameters = "Pooling = true; Minimum Pool Size = 1; Maximum Pool Size = 100;Max Auto Prepare = 20";
				connectionString = $"Server={host};Port=5432;Database={database};User Id={user};Password={password};{parameters}";
			}

			return connectionString;
		}
	}
}