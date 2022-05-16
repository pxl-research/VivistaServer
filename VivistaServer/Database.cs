using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

		public static async Task<IEnumerable<T>> QueryAsync<T>(NpgsqlConnection conn, string sql, HttpContext context, object param = null, IDbTransaction transaction = null)
		{
			var watch = Stopwatch.StartNew();
			var result = await conn.QueryAsync<T>(sql, param, transaction);
			watch.Stop();
			DashboardController.AddDbExecTimeToRequest(context, watch.Elapsed.TotalSeconds);
			return result;
		}

		public static async Task<int> ExecuteAsync(NpgsqlConnection conn, string sql, HttpContext context, object param = null, IDbTransaction transaction = null)
		{
			var watch = Stopwatch.StartNew();
			var result = await conn.ExecuteAsync(sql, param, transaction);
			watch.Stop();
			DashboardController.AddDbExecTimeToRequest(context, watch.Elapsed.TotalSeconds);
			return result;
		}

		public static async Task<T> QuerySingleAsync<T>(NpgsqlConnection conn, string sql, HttpContext context, object param = null, IDbTransaction transaction = null)
		{
			var watch = Stopwatch.StartNew();
			var result = await conn.QuerySingleAsync<T>(sql, param, transaction);
			watch.Stop();
			DashboardController.AddDbExecTimeToRequest(context, watch.Elapsed.TotalSeconds);
			return result;
		}

		public static async Task<T> QueryFirstOrDefaultAsync<T>(NpgsqlConnection conn, string sql, HttpContext context, object param = null, IDbTransaction transaction = null)
		{
			var watch = Stopwatch.StartNew();
			var result = await conn.QueryFirstOrDefaultAsync<T>(sql, param, transaction);
			watch.Stop();
			DashboardController.AddDbExecTimeToRequest(context, watch.Elapsed.TotalSeconds);
			return result;
		}

		public static async Task<T> QuerySingleOrDefaultAsync<T>(NpgsqlConnection conn, string sql, HttpContext context, object param = null, IDbTransaction transaction = null)
		{
			var watch = Stopwatch.StartNew();
			var result = await conn.QuerySingleOrDefaultAsync<T>(sql, param, transaction);
			watch.Stop();
			DashboardController.AddDbExecTimeToRequest(context, watch.Elapsed.TotalSeconds);
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

		public static void PerformMigrations()
		{
			using var conn = OpenNewConnection();

			try
			{
				CreateMigrationTableIfNecessary(conn);
			}
			catch (Exception e)
			{
				Console.WriteLine(e.ToString());
			}

			var fileInfos = new DirectoryInfo("Migrations").GetFiles("*.sql");
			var files = fileInfos.Select(x => x.Name[.. x.Name.LastIndexOf('.')]).AsList();


			//NOTE(Simon): Reverse Sort, so newest is first. Filename contains migration timestamp
			files.Sort((a, b) => String.Compare(b, a, StringComparison.Ordinal));

			//NOTE(Simon): Get most recently performed migration
			string latest = conn.QuerySingleOrDefault<string>("SELECT * FROM migrations ORDER BY name DESC LIMIT 1");

			int index = files.IndexOf(latest);

			using var transaction = conn.BeginTransaction();

			try
			{
				//NOTE(Simon): Perform all migrations that have not been added to this db. From oldest to newest, so a "reverse" loop
				for (int i = index - 1; i >= 0; i--)
				{
					string script = File.ReadAllText(Path.Combine("Migrations", files[i] + ".sql"));

					var command = new NpgsqlCommand(script, conn, transaction);
					command.ExecuteNonQuery();
					//NOTE(Simon): Log the migration we just performed
					conn.Execute(@"INSERT INTO migrations (name, time) VALUES (@name, NOW())", new { name = files[i] });
				}

				transaction.Commit();
			}
			catch (Exception e)
			{
				Console.WriteLine(e.ToString());
				transaction.Rollback();
				Environment.Exit(-1);
			}
		}

		private static void CreateMigrationTableIfNecessary(NpgsqlConnection conn)
		{
			var command = new NpgsqlCommand(@"CREATE TABLE IF NOT EXISTS public.migrations 
												(name text, time timestamp, PRIMARY KEY (name));", conn);
			command.ExecuteNonQuery();
		}
	}
}
