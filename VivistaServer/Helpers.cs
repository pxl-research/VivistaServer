using System;
using System.Text;
using System.Globalization;

namespace VivistaServer
{
	public static class StringHelpers
	{
		public static string NormalizeEmail(this string email)
		{
			return email.ToLowerInvariant().Trim();
		}

		public static string NormalizeForSearch(this string str)
		{
			string stFormD = str.Normalize(NormalizationForm.FormD);
			int len = stFormD.Length;
			var sb = new StringBuilder();

			for (int i = 0; i < len; i++)
			{
				var uc = CharUnicodeInfo.GetUnicodeCategory(stFormD[i]);
				if (uc != UnicodeCategory.NonSpacingMark)
				{
					sb.Append(stFormD[i]);
				}
			}
			return sb.ToString().Normalize(NormalizationForm.FormC);
		}
	}

	public static class GuidHelpers
	{
		public static string Encode(this Guid guid)
		{
			string encoded = Convert.ToBase64String(guid.ToByteArray());
			encoded = encoded.Replace("/", "_").Replace("+", "-");
			return encoded.Substring(0, 22);
		}

		public static bool TryDecode(string value, out Guid guid)
		{
			value = value.Replace("_", "/").Replace("-", "+");
			byte[] buffer = Convert.FromBase64String(value + "==");
			try
			{
				guid = new Guid(buffer);
				return true;
			}
			catch
			{
				guid = new Guid();
				return false;
			}
		}
	}

	public static class FormatHelpers
	{
		public static String FormatBytesToString(long rawBytes)
		{
			string[] suf = { "B", "KB", "MB", "GB", "TB", "PB", "EB" }; //Longs run out around EB
			if (rawBytes == 0)
			{
				return $"0{suf[0]}";
			}

			long bytes = Math.Abs(rawBytes);
			int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
			double num = Math.Round(bytes / Math.Pow(1024, place), 1);
			return $"{Math.Sign(rawBytes) * num}{suf[place]}";
		}

		public static String BytesToString(decimal rawBytes)
		{
			return FormatBytesToString((long)rawBytes);
		}

		public static String FormatSecondsToString(int time)
		{
			int hours = time / (60 * 60);
			time -= hours * 60 * 60;
			int minutes = time / 60;
			time -= minutes * 60;
			int seconds = time;

			string formatted = "";
			if (hours > 0)
			{
				formatted += $"{hours}:";
			}

			formatted += $"{minutes:D2}:{seconds:D2}";

			return formatted;
		}

		public static String FormatSecondsToString(decimal seconds)
		{
			return FormatSecondsToString((int)seconds);
		}
	}
}
