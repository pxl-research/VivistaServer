using System;
using System.Security.Cryptography;

namespace VivistaServer
{
	public class Tokens
	{
		public static RNGCryptoServiceProvider rng;

		static Tokens()
		{
			rng = new RNGCryptoServiceProvider();
		}

		public static string NewToken(int numBytes)
		{
			var bytes = new byte[numBytes];
			rng.GetBytes(bytes);
			return Convert.ToBase64String(bytes).Substring(0, numBytes);
		}

		public static string NewSessionToken()
		{
			return NewToken(32);
		}

		public static string NewVerifyEmailToken()
		{
			return NewToken(16);
		}

		public static string NewPasswordResetToken()
		{
			return NewToken(32);
		}
	}
}
