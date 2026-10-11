using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace CelesteAndroid
{
	/// <summary>Latest stable Everest release returned by the GitHub Releases API.</summary>
	internal sealed record EverestRelease(string Tag, string DownloadUrl, long Size, string? Sha256);

	/// <summary>
	/// Resolves main.zip from the current stable GitHub release rather than a permanently pinned URL.
	/// The selected version is recorded for this installation, so an installed game doesn't update
	/// unexpectedly on every launch.
	/// </summary>
	internal static class EverestReleaseClient
	{
		private const string LatestReleaseUrl = "https://api.github.com/repos/EverestAPI/Everest/releases/latest";
		private const long MaxDownloadBytes = 1L << 30;
		private static readonly HttpClient http = CreateHttpClient();

		private static HttpClient CreateHttpClient()
		{
			var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
			client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
			client.DefaultRequestHeaders.UserAgent.ParseAdd("CelesteAndroid/1.0");
			client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
			client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
			return client;
		}

		public static EverestRelease GetLatest(Action<string, float> progress)
		{
			progress("Looking up the latest stable Everest…", -1);
			using JsonDocument releaseJson = GetLatestRelease();
			JsonElement release = releaseJson.RootElement;
			string tag = RequiredString(release, "tag_name");

			JsonElement asset = default;
			foreach (JsonElement candidate in release.GetProperty("assets").EnumerateArray())
			{
				if (candidate.TryGetProperty("name", out JsonElement name)
					&& name.GetString() == "main.zip")
				{
					asset = candidate;
					break;
				}
			}
			if (asset.ValueKind == JsonValueKind.Undefined)
				throw new InstallException($"The latest Everest release ({tag}) has no main.zip asset.");

			long size = asset.GetProperty("size").GetInt64();
			if (size <= 0 || size > MaxDownloadBytes)
				throw new InstallException("The latest Everest main.zip has an invalid or unsafe size.");
			string url = RequiredString(asset, "browser_download_url");
			string? digest = asset.TryGetProperty("digest", out JsonElement digestElement)
				? digestElement.GetString()
				: null;
			if (digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true)
				digest = digest[7..];
			else
				digest = null;
			return new EverestRelease(tag, url, size, digest);
		}

		public static void Download(EverestRelease release, string destination, Action<string, float> progress)
		{
			progress($"Downloading Everest {release.Tag}…", -1);
			DownloadAsset(release.DownloadUrl, destination, release.Size, release.Sha256, progress);
		}

		private static JsonDocument GetLatestRelease()
		{
			try
			{
				using HttpResponseMessage response = http.GetAsync(LatestReleaseUrl, HttpCompletionOption.ResponseHeadersRead)
					.GetAwaiter().GetResult();
				if (!response.IsSuccessStatusCode)
				{
					throw new InstallException(
						$"GitHub couldn't return the latest Everest release (HTTP {(int)response.StatusCode}). " +
						"Check the internet connection and try again later.");
				}
				using Stream body = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
				return JsonDocument.Parse(body);
			}
			catch (InstallException)
			{
				throw;
			}
			catch (Exception e)
			{
				throw new InstallException("Couldn't check for the latest Everest release: " + e.Message);
			}
		}

		private static void DownloadAsset(
			string url,
			string destination,
			long expectedSize,
			string? expectedSha256,
			Action<string, float> progress)
		{
			string partial = destination + ".partial";
			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			try
			{
				using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
				using HttpResponseMessage response = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
					.GetAwaiter().GetResult();
				if (!response.IsSuccessStatusCode)
					throw new InstallException($"Couldn't download Everest main.zip (HTTP {(int)response.StatusCode}).");

				using Stream input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
				using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
				byte[] buffer = new byte[1 << 16];
				long downloaded = 0;
				int read;
				while ((read = input.ReadAsync(buffer.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult()) > 0)
				{
					downloaded += read;
					if (downloaded > MaxDownloadBytes)
						throw new InstallException("Everest main.zip exceeds the 1 GB download safety limit.");
					output.Write(buffer, 0, read);
					if (expectedSize > 0)
						progress($"Downloading Everest main.zip… {downloaded / (1 << 20)} MB", Math.Min(1f, downloaded / (float)expectedSize));
				}
				output.Flush();

				if (expectedSize > 0 && downloaded != expectedSize)
					throw new InstallException($"Everest download is incomplete ({downloaded} of {expectedSize} bytes).");
				if (downloaded < 4)
					throw new InstallException("Everest download is empty or isn't a ZIP file.");

				using (FileStream check = File.OpenRead(partial))
				{
					int first = check.ReadByte();
					int second = check.ReadByte();
					if (first != 0x50 || second != 0x4B)
						throw new InstallException("The downloaded Everest main.zip isn't a ZIP file.");
				}

				if (expectedSha256 != null)
				{
					using FileStream check = File.OpenRead(partial);
					string actual = Convert.ToHexString(SHA256.HashData(check));
					if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
						throw new InstallException("Everest main.zip failed its SHA-256 integrity check.");
				}

				File.Move(partial, destination, overwrite: true);
				progress("Everest archive verified.", 1f);
			}
			catch (InstallException)
			{
				TryDelete(partial);
				throw;
			}
			catch (Exception e)
			{
				TryDelete(partial);
				throw new InstallException("Couldn't download Everest main.zip: " + e.Message);
			}
		}

		private static string RequiredString(JsonElement element, string property)
		{
			if (!element.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String)
				throw new InstallException($"GitHub's Everest release response is missing '{property}'.");
			return value.GetString()!;
		}

		private static void TryDelete(string path)
		{
			try
			{
				if (File.Exists(path))
					File.Delete(path);
			}
			catch (Exception)
			{
				// A failed cleanup must not hide the original download or verification error.
			}
		}
	}
}
