using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Http;
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

        public static Task<IEnumerable<T>> QueryAsync<T>(NpgsqlConnection conn, string sql, HttpContext context, object param = null)
        {
            var watch = Stopwatch.StartNew();
			var result =  conn.QueryAsync<T>(sql, param);
			watch.Stop();
            DashboardController.AddDbExecTimeToRequest(context, watch.Elapsed.TotalMilliseconds);
            return result;
        }

        public static Task<int> ExecuteAsync(NpgsqlConnection conn, string sql, HttpContext context, object param = null)
        {
            var watch = Stopwatch.StartNew();
            var result = conn.ExecuteAsync(sql, param);
            watch.Stop();
            DashboardController.AddDbExecTimeToRequest(context, watch.Elapsed.TotalMilliseconds);
            return result;
        }

        public static IEnumerable<dynamic> Query(NpgsqlConnection conn, string sql, HttpContext context, object param = null)
        {
            var watch = Stopwatch.StartNew();
            var result = conn.Query(sql, param);
            watch.Stop();
            DashboardController.AddDbExecTimeToRequest(context, watch.Elapsed.TotalMilliseconds);
            return result;
        }

        public static int Execute(NpgsqlConnection conn, string sql, HttpContext context, object param = null)
        {
            var watch = Stopwatch.StartNew();
            var result = conn.Execute(sql, param);
            watch.Stop();
            DashboardController.AddDbExecTimeToRequest(context, watch.Elapsed.TotalMilliseconds);
			return result;
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