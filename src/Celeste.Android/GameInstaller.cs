using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Android.Content;
using Android.Database;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Util;
using CelesteAndroid.Patcher;
using Microsoft.Win32.SafeHandles;
using Path = System.IO.Path;
using Uri = Android.Net.Uri;

namespace CelesteAndroid
{
	/// <summary>
	/// Importa os arquivos do jogo do usuário para o armazenamento interno do app e prepara tudo para rodar:
	/// cópia (pasta, .zip ou assets do APK pessoal) → patch com MonoMod → fundo das faixas laterais.
	/// </summary>
	public sealed class GameInstaller
	{
		private const string GameAssetsRoot = "game";
		private const string PatcherAssetName = "Celeste.Android.mm.dll";
		private const int BufferSize = 1 << 20;
		private const long MarginBytes = 256L << 20;
		private const int MaxArchiveEntries = 100_000;
		private const long MaxEverestArchiveBytes = 1L << 30;

		private readonly Context context;
		private readonly Action<string, float> progress;
		private readonly byte[] buffer = new byte[BufferSize];
		private readonly string gameDir;
		private readonly string patchedDir;
		private readonly string backgroundPath;
		private string? everestTag;
		private string? everestSha256;

		public GameInstaller(Context context, Action<string, float> progress)
			: this(context, progress, GameDir(context), Path.Combine(context.FilesDir!.AbsolutePath, "patched"), BackgroundPng(context))
		{
		}

		private GameInstaller(Context context, Action<string, float> progress, string gameDir, string patchedDir, string backgroundPath)
		{
			this.context = context;
			this.progress = progress;
			this.gameDir = gameDir;
			this.patchedDir = patchedDir;
			this.backgroundPath = backgroundPath;
		}

		private static string Files(Context context) => context.FilesDir!.AbsolutePath;
		public static string GameDir(Context context) => Path.Combine(Files(context), "Celeste");
		public static string PatchedDll(Context context) => Path.Combine(Files(context), "patched", "Celeste.dll");
		public static string BackgroundPng(Context context) => Path.Combine(Files(context), "background.png");
		public static string UserDir(Context context) => Path.Combine(Files(context), "userdata");

		/// <summary>Cria uma transação isolada: a instalação atual permanece utilizável até o commit.</summary>
		public GameInstaller CreateStaging()
		{
			string files = Files(context);
			return new GameInstaller(
				context,
				progress,
				GameDir(context) + ".installing",
				Path.Combine(files, "patched.installing"),
				Path.Combine(files, "background.installing.png"));
		}

		public void ClearStaging()
		{
			TryDeleteDirectory(gameDir + ".importing");
			TryDeleteDirectory(gameDir + ".previous");
			TryDeleteDirectory(gameDir);
			TryDeleteDirectory(patchedDir + ".previous");
			TryDeleteDirectory(patchedDir);
			TryDeleteFile(backgroundPath + ".previous");
			TryDeleteFile(backgroundPath);
			if (Directory.Exists(gameDir + ".importing") || Directory.Exists(gameDir + ".previous")
				|| Directory.Exists(gameDir) || Directory.Exists(patchedDir + ".previous")
				|| Directory.Exists(patchedDir) || File.Exists(backgroundPath + ".previous") || File.Exists(backgroundPath))
				throw new InstallException("Couldn't clear an unfinished staged installation. Check storage permissions and free space.");
		}

		public void CommitInstall(bool useEverest)
		{
			string activeGame = GameDir(context);
			string activePatches = Path.Combine(Files(context), "patched");
			string activeBackground = BackgroundPng(context);
			string oldGame = activeGame + ".previous";
			string oldPatches = activePatches + ".previous";
			string oldBackground = activeBackground + ".previous";
			string manifestPath = InstallManifestPath(context);
			string oldManifest = manifestPath + ".previous";
			TryDeleteDirectory(oldGame);
			TryDeleteDirectory(oldPatches);
			TryDeleteFile(oldBackground);
			TryDeleteFile(oldManifest);

			bool movedOldGame = false;
			bool movedOldPatches = false;
			bool movedNewGame = false;
			bool movedNewPatches = false;
			bool movedOldBackground = false;
			bool movedNewBackground = false;
			bool movedOldManifest = false;
			bool movedNewManifest = false;
			try
			{
				if (Directory.Exists(activeGame))
				{
					Directory.Move(activeGame, oldGame);
					movedOldGame = true;
				}
				if (Directory.Exists(activePatches))
				{
					Directory.Move(activePatches, oldPatches);
					movedOldPatches = true;
				}
				if (File.Exists(activeBackground))
				{
					File.Move(activeBackground, oldBackground);
					movedOldBackground = true;
				}

				Directory.Move(gameDir, activeGame);
				movedNewGame = true;
				Directory.Move(patchedDir, activePatches);
				movedNewPatches = true;
				if (File.Exists(backgroundPath))
				{
					File.Move(backgroundPath, activeBackground);
					movedNewBackground = true;
				}

				var manifest = new InstallManifest
				{
					Mode = useEverest ? "everest" : "vanilla",
					EverestTag = useEverest ? everestTag : null,
					EverestSha256 = useEverest ? everestSha256 : null,
				};
				string tempManifest = manifestPath + ".tmp";
				if (File.Exists(manifestPath))
				{
					File.Move(manifestPath, oldManifest);
					movedOldManifest = true;
				}
				File.WriteAllText(tempManifest, JsonSerializer.Serialize(manifest));
				File.Move(tempManifest, manifestPath);
				movedNewManifest = true;

				TryDeleteDirectory(oldGame);
				TryDeleteDirectory(oldPatches);
				TryDeleteFile(oldBackground);
				TryDeleteFile(oldManifest);
			}
			catch
			{
				if (movedNewManifest)
					TryDeleteFile(manifestPath);
				TryDeleteFile(manifestPath + ".tmp");
				if (movedNewBackground)
					TryDeleteFile(activeBackground);
				if (movedNewPatches)
					TryDeleteDirectory(activePatches);
				if (movedNewGame)
					TryDeleteDirectory(activeGame);
				if (movedOldGame && !Directory.Exists(activeGame))
					Directory.Move(oldGame, activeGame);
				if (movedOldPatches && !Directory.Exists(activePatches))
					Directory.Move(oldPatches, activePatches);
				if (movedOldBackground && !File.Exists(activeBackground))
					File.Move(oldBackground, activeBackground);
				if (movedOldManifest && !File.Exists(manifestPath))
					File.Move(oldManifest, manifestPath);
				throw;
			}
		}

		private sealed class InstallManifest
		{
			public InstallManifest() { }

			public int Schema { get; set; } = 1;
			public string Mode { get; set; } = "vanilla";
			public string? EverestTag { get; set; }
			public string? EverestSha256 { get; set; }
		}

		private static void TryDeleteFile(string path)
		{
			try
			{
				if (File.Exists(path))
					File.Delete(path);
			}
			catch (Exception e)
			{
				Log.Warn(GameActivity.LogTag, $"Couldn't remove {path}: {e.Message}");
			}
		}

		/// <summary>Assinatura do patch que gerou o Celeste.dll instalado (ver IsPatchCurrent).</summary>
		public static string PatchStampPath(Context context) =>
			Path.Combine(Files(context), "patched", "patch.stamp");

		private static string InstallManifestPath(Context context) => Path.Combine(Files(context), "install.json");

		public static bool IsInstalled(Context context) =>
			File.Exists(PatchedDll(context)) && Directory.Exists(Path.Combine(GameDir(context), "Content"));

		public static bool IsEverestInstalled(Context context) =>
			string.Equals(ReadInstallManifest(context)?.Mode, "everest", StringComparison.OrdinalIgnoreCase);

		public static string? InstalledEverestVersion(Context context) => ReadInstallManifest(context)?.EverestTag;

		/// <summary>
		/// Includes the port module, source assembly and install mode. In Everest mode, the source is
		/// MiniInstaller's intermediate Celeste.dll, not the port-patched output.
		/// </summary>
		public static bool IsPatchCurrent(Context context)
		{
			string stamp = PatchStampPath(context);
			if (!File.Exists(stamp))
				return false;
			string? current = CurrentPatchStamp(context);
			return current != null && File.ReadAllText(stamp).Trim() == current;
		}

		public static string? CurrentPatchStamp(Context context)
		{
			bool everest = IsEverestInstalled(context);
			string source = Path.Combine(GameDir(context), everest ? "Celeste.dll" : "Celeste.exe");
			if (!File.Exists(source))
				return null;

			try
			{
				using Stream module = context.Assets!.Open("patcher/" + PatcherAssetName);
				string moduleHash = Hash(module);
				string fingerprint = $"v2|{(everest ? "everest" : "vanilla")}|{moduleHash}|{HashFile(source)}";
				return HashText(fingerprint);
			}
			catch (Exception e)
			{
				Log.Warn(GameActivity.LogTag, "Couldn't compute the current game patch stamp: " + e.Message);
				return null;
			}
		}

		private static InstallManifest? ReadInstallManifest(Context context)
		{
			try
			{
				string path = InstallManifestPath(context);
				return File.Exists(path) ? JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path)) : null;
			}
			catch (Exception e)
			{
				Log.Warn(GameActivity.LogTag, "Couldn't read install metadata: " + e.Message);
				return null;
			}
		}

		private static string Hash(Stream stream) => Convert.ToHexString(SHA256.HashData(stream))[..16];

		private static string HashText(string text) =>
			Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

		private static string HashFile(string path)
		{
			using FileStream stream = File.OpenRead(path);
			return Hash(stream);
		}

		/// <summary>APK pessoal: o jogo vem nos assets (build com -p:EmbedGame=true).</summary>
		public static bool HasEmbeddedGame(Context context) =>
			context.Assets!.List(GameAssetsRoot)?.Contains("Celeste.exe") == true;

		#region Importação

		/// <summary>Pasta escolhida pelo usuário (ACTION_OPEN_DOCUMENT_TREE).</summary>
		public void ImportFolder(Uri treeUri, bool includeManagedDependencies = false)
		{
			progress("Looking for Celeste in the folder…", -1);
			ContentResolver resolver = context.ContentResolver!;
			string rootId = DocumentsContract.GetTreeDocumentId(treeUri)!;

			Doc root = FindGameRoot(treeUri, new Doc(rootId, "", true, 0), depth: 3)
				?? throw new InstallException("Couldn't find Celeste.exe and the Content folder there.");
			List<Doc> top = ListChildren(treeUri, root.Id);
			CheckFnaBuild(top.Any(d => d.Name.Equals("FNA.dll", StringComparison.OrdinalIgnoreCase)));

			var files = new List<(Doc doc, string relative)>();
			files.Add((top.First(d => d.Name.Equals("Celeste.exe", StringComparison.OrdinalIgnoreCase)), "Celeste.exe"));
			if (includeManagedDependencies)
			{
				foreach (Doc dependency in top.Where(d => !d.IsDir
					&& !d.Name.Equals("Celeste.exe", StringComparison.OrdinalIgnoreCase)
					&& (d.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
						|| d.Name.EndsWith(".config", StringComparison.OrdinalIgnoreCase))))
					files.Add((dependency, dependency.Name.Equals("FNA.dll", StringComparison.OrdinalIgnoreCase) ? "FNA.dll" : dependency.Name));
			}
			CollectFiles(treeUri, top.First(d => d.Name.Equals("Content", StringComparison.OrdinalIgnoreCase) && d.IsDir), "Content", files);

			long total = 0;
			foreach ((Doc doc, string _) in files)
			{
				if (doc.Size < 0)
				{
					total = -1;
					break;
				}
				total += doc.Size;
			}
			EnsureFreeSpace(total < 0 ? null : total);

			CopyAll(files.Select(f => (f.relative, f.doc.Size,
				(Func<Stream>)(() => resolver.OpenInputStream(DocumentsContract.BuildDocumentUriUsingTree(treeUri, f.doc.Id)!)!))));
		}

		/// <summary>.zip escolhido pelo usuário (ex.: o zip Linux do itch.io).</summary>
		public void ImportZip(Uri zipUri, bool includeManagedDependencies = false)
		{
			progress("Reading the .zip…", -1);
			using var pfd = context.ContentResolver!.OpenFileDescriptor(zipUri, "r")
				?? throw new InstallException("Couldn't open the file.");
			using var stream = new FileStream(new SafeFileHandle(pfd.DetachFd(), ownsHandle: true), FileAccess.Read);
			using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

			ZipArchiveEntry exe = zip.Entries
				.Where(e => e.Name.Equals("Celeste.exe", StringComparison.OrdinalIgnoreCase))
				.OrderBy(e => e.FullName.Length)
				.FirstOrDefault() ?? throw new InstallException("This .zip doesn't contain Celeste.exe.");
			string prefix = exe.FullName[..^exe.Name.Length];
			CheckFnaBuild(zip.Entries.Any(e => e.FullName.Equals(prefix + "FNA.dll", StringComparison.OrdinalIgnoreCase)));

			var entries = zip.Entries
				.Where(e => e == exe
					|| (e.FullName.StartsWith(prefix + "Content/", StringComparison.OrdinalIgnoreCase) && e.Name.Length > 0)
					|| (includeManagedDependencies
						&& e.FullName.StartsWith(prefix, StringComparison.Ordinal)
						&& !e.FullName[prefix.Length..].Contains('/')
						&& (e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
							|| e.Name.EndsWith(".config", StringComparison.OrdinalIgnoreCase))))
				.ToList();
			EnsureFreeSpace(entries.Sum(e => e.Length));
			CopyAll(entries.Select(e =>
			{
				string relative = e.FullName[prefix.Length..];
				if (relative.StartsWith("Content/", StringComparison.OrdinalIgnoreCase))
					relative = "Content/" + relative["Content/".Length..];
				else if (!relative.Contains('/') && e.Name.Equals("Celeste.exe", StringComparison.OrdinalIgnoreCase))
					relative = "Celeste.exe";
				else if (!relative.Contains('/') && e.Name.Equals("FNA.dll", StringComparison.OrdinalIgnoreCase))
					relative = "FNA.dll";
				return (relative, e.Length, (Func<Stream>)e.Open);
			}));
		}

		/// <summary>APK pessoal: copia o jogo embutido nos assets.</summary>
		public void ImportEmbedded()
		{
			progress("Preparing the bundled game…", -1);
			// O APK é praticamente o jogo embutido: serve de estimativa (conservadora) do tamanho.
			EnsureFreeSpace(ApkBytes());
			var files = new List<string>();
			CollectAssets(GameAssetsRoot, files);
			CopyAll(files.Select(f => (f[(GameAssetsRoot.Length + 1)..], -1L, (Func<Stream>)(() => context.Assets!.Open(f)))));
		}

		/// <summary>Baixa o último stable e executa os estágios managed do MiniInstaller no diretório de staging.</summary>
		public string InstallLatestEverest()
		{
			if (!File.Exists(Path.Combine(gameDir, "Celeste.exe")) || !File.Exists(Path.Combine(gameDir, "FNA.dll")))
				throw new InstallException("Everest needs the FNA PC build: import Celeste.exe, FNA.dll and Content/ together.");

			EverestRelease release = EverestReleaseClient.GetLatest(progress);
			EnsureFreeSpace(release.Size);
			string archivePath = Path.Combine(context.CacheDir!.AbsolutePath, "everest-main.zip");
			try
			{
				EverestReleaseClient.Download(release, archivePath, progress);
				string archiveSha256 = HashFullFile(archivePath);
				ExtractEverestArchive(archivePath);

				string patcherDir = ExtractPatcherAssets();
				// MiniInstaller's MonoMod passes need the Android no-op Steamworks reference in the game
				// directory; its desktop setup stage (which normally chooses this DLL) is intentionally skipped.
				string androidSteamworks = Path.Combine(patcherDir, "Steamworks.NET.dll");
				if (File.Exists(androidSteamworks))
					File.Copy(androidSteamworks, Path.Combine(gameDir, "Steamworks.NET.dll"), overwrite: true);

				string toolRoot = Path.Combine(context.CacheDir!.AbsolutePath, "everest-miniinstaller");
				string tempRoot = Path.Combine(context.CacheDir!.AbsolutePath, "everest-minitmp");
				EverestMiniInstaller.Install(gameDir, Path.Combine(patcherDir, "FNA.dll"), toolRoot, tempRoot, progress);

				everestTag = release.Tag;
				everestSha256 = archiveSha256;
				return release.Tag;
			}
			finally
			{
				TryDeleteFile(archivePath);
			}
		}

		private void ExtractEverestArchive(string archivePath)
		{
			progress("Unpacking Everest files…", -1);
			using FileStream stream = File.OpenRead(archivePath);
			using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
			if (zip.Entries.Count > MaxArchiveEntries)
				throw new InstallException("Everest archive contains too many files.");

			ZipArchiveEntry mod = zip.Entries
				.Where(e => e.Name.Equals("Celeste.Mod.mm.dll", StringComparison.OrdinalIgnoreCase))
				.OrderBy(e => e.FullName.Length)
				.FirstOrDefault() ?? throw new InstallException("Everest main.zip doesn't contain Celeste.Mod.mm.dll.");
			string prefix = mod.FullName[..^mod.Name.Length];
			long expandedBytes = 0;
			foreach (ZipArchiveEntry entry in zip.Entries)
			{
				if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal))
					continue;
				expandedBytes = checked(expandedBytes + entry.Length);
				if (expandedBytes > MaxEverestArchiveBytes)
					throw new InstallException("Everest main.zip expands beyond the 1 GB safety limit.");
			}
			EnsureFreeSpace(expandedBytes);

			int copied = 0;
			int total = zip.Entries.Count(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.Name.Length > 0);
			foreach (ZipArchiveEntry entry in zip.Entries)
			{
				if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal))
					continue;
				string relative = entry.FullName[prefix.Length..];
				if (relative.Length == 0)
					continue;
				RejectZipLink(entry);

				// Keep the user's Celeste/Content and original PC FNA untouched; the Android
				// installer writes its own managed outputs after the Everest patch stage.
				string first = relative.Replace('\\', '/').Split('/')[0];
				if (first.Equals("Content", StringComparison.OrdinalIgnoreCase)
					|| relative.Equals("Celeste.exe", StringComparison.OrdinalIgnoreCase)
					|| relative.Equals("FNA.dll", StringComparison.OrdinalIgnoreCase)
					|| relative.Equals("Celeste.dll", StringComparison.OrdinalIgnoreCase))
					continue;

				bool isDirectory = entry.Name.Length == 0;
				string safeRelative = isDirectory ? relative.TrimEnd('/') : relative;
				if (safeRelative.Length == 0)
					continue;
				string destination = SafeDestination(gameDir, safeRelative);
				if (isDirectory)
				{
					Directory.CreateDirectory(destination);
					continue;
				}
				Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
				using Stream input = entry.Open();
				using FileStream output = File.Create(destination);
				input.CopyTo(output, BufferSize);
				copied++;
				if (copied % 16 == 0 || copied == total)
					progress($"Unpacking Everest files… {copied}/{total}", total == 0 ? 1f : copied / (float)total);
			}
		}

		private static void RejectZipLink(ZipArchiveEntry entry)
		{
			int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
			if (unixType == 0xA000)
				throw new InstallException("Archive contains a symbolic link, which isn't allowed.");
		}

		private static string HashFullFile(string path)
		{
			using FileStream stream = File.OpenRead(path);
			return Convert.ToHexString(SHA256.HashData(stream));
		}

		/// <summary>Copies an Everest mod archive into the installed Mods directory without extracting it.</summary>
		public string ImportEverestMod(Uri modUri)
		{
			if (!IsEverestInstalled(context))
				throw new InstallException("Install Everest first before importing mods.");

			string displayName = "";
			using (ICursor? cursor = context.ContentResolver!.Query(modUri, new[] { Android.Provider.OpenableColumns.DisplayName }, null, null, null))
			{
				if (cursor != null && cursor.MoveToFirst())
				{
					int column = cursor.GetColumnIndex(Android.Provider.OpenableColumns.DisplayName);
					if (column >= 0)
						displayName = cursor.GetString(column) ?? "";
				}
			}
			if (string.IsNullOrWhiteSpace(displayName))
				displayName = modUri.LastPathSegment ?? "mod.zip";

			string basename = Path.GetFileName(displayName.Replace('\\', '/'));
			char[] invalid = Path.GetInvalidFileNameChars();
			string fileName = new string(basename.Select(ch => ch == '/' || ch == '\\' || invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
			if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
				throw new InstallException("Choose a zipped Everest mod (.zip).");
			if (fileName.Length > 180)
				fileName = fileName[..176] + ".zip";
			if (fileName.Length <= 4 || fileName is ".zip" or "..zip")
				throw new InstallException("The selected mod has an invalid file name.");

			string modsDir = Path.Combine(GameDir(context), "Mods");
			Directory.CreateDirectory(modsDir);
			string destination = Path.Combine(modsDir, fileName);
			string partial = destination + ".partial";
			TryDeleteFile(partial);
			try
			{
				progress($"Copying Everest mod {fileName}…", -1);
				using Stream input = context.ContentResolver.OpenInputStream(modUri)
					?? throw new InstallException("Couldn't open the selected mod archive.");
				using (FileStream output = File.Create(partial))
				{
					long copied = 0;
					int read;
					while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
					{
						copied += read;
						if (copied > MaxEverestArchiveBytes)
							throw new InstallException("The mod archive exceeds the 1 GB safety limit.");
						output.Write(buffer, 0, read);
					}
					if (copied < 4)
						throw new InstallException("The selected mod is empty or isn't a ZIP archive.");
				}

				using (FileStream archiveStream = File.OpenRead(partial))
				using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read))
				{
					if (archive.Entries.Count == 0 || archive.Entries.Count > MaxArchiveEntries)
						throw new InstallException("The selected mod archive is empty or contains too many files.");
					foreach (ZipArchiveEntry entry in archive.Entries)
						RejectZipLink(entry);
				}
				File.Move(partial, destination, overwrite: true);
				progress("Everest mod imported.", 1f);
				return fileName;
			}
			catch (InstallException)
			{
				TryDeleteFile(partial);
				throw;
			}
			catch (Exception e)
			{
				TryDeleteFile(partial);
				throw new InstallException("Couldn't import this Everest mod: " + e.Message);
			}
		}

		/// <summary>
		/// Saves do PC (ou de outro aparelho): uma pasta com os .celeste, ou a pasta do jogo com Saves/ dentro.
		/// Os saves atuais vão para Backups/ antes de serem substituídos.
		/// </summary>
		public int ImportSaves(Uri treeUri)
		{
			progress("Looking for save files…", -1);
			string rootId = DocumentsContract.GetTreeDocumentId(treeUri)!;
			List<Doc> files = ListChildren(treeUri, rootId);
			Doc? savesDir = files.FirstOrDefault(d => d.IsDir && d.Name.Equals("Saves", StringComparison.OrdinalIgnoreCase));
			if (savesDir is Doc dir)
				files = ListChildren(treeUri, dir.Id);
			List<Doc> saves = files.Where(d => !d.IsDir && d.Name.EndsWith(".celeste", StringComparison.OrdinalIgnoreCase)).ToList();
			if (saves.Count == 0)
				throw new InstallException("No .celeste save files in that folder.");

			string target = Path.Combine(UserDir(context), "Celeste", "Saves");
			string backup = Path.Combine(UserDir(context), "Celeste", "Backups");
			Directory.CreateDirectory(target);
			Directory.CreateDirectory(backup);
			foreach (string existing in Directory.GetFiles(target, "*.celeste"))
				File.Copy(existing, Path.Combine(backup, Path.GetFileName(existing)), overwrite: true);

			foreach (Doc save in saves)
			{
				using Stream input = context.ContentResolver!.OpenInputStream(DocumentsContract.BuildDocumentUriUsingTree(treeUri, save.Id)!)!;
				using FileStream output = File.Create(Path.Combine(target, save.Name));
				input.CopyTo(output);
			}
			return saves.Count;
		}

		private void CheckFnaBuild(bool hasFna)
		{
			if (!hasFna)
			{
				throw new InstallException(
					"This is the XNA version of Celeste. On Steam, enable the \"opengl\" beta " +
					"(Properties → Betas) and copy the folder again, or use the Linux .zip from itch.io.");
			}
		}

		private static string SafeDestination(string root, string relative)
		{
			string normalized = relative.Replace('\\', '/');
			if (string.IsNullOrWhiteSpace(normalized) || normalized[0] == '/'
				|| normalized.Contains(':') || normalized.Contains('\0'))
				throw new InstallException("Archive contains an invalid path.");
			string[] segments = normalized.Split('/');
			if (segments.Any(segment => segment.Length == 0 || segment == "." || segment == ".."))
				throw new InstallException("Archive contains a path that escapes its install folder.");

			string rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
			string fullPath = Path.GetFullPath(Path.Combine(rootPath, normalized.Replace('/', Path.DirectorySeparatorChar)));
			if (!fullPath.StartsWith(rootPath, StringComparison.Ordinal))
				throw new InstallException("Archive contains a path that escapes its install folder.");
			return fullPath;
		}

		private void CopyAll(IEnumerable<(string relative, long size, Func<Stream> open)> source)
		{
			var files = source.ToList();
			if (files.Count == 0)
				throw new InstallException("No game files were selected for import.");
			long total = Math.Max(1, files.Sum(f => Math.Max(0, f.size)));
			long done = 0;

			// Copy beside the destination first; the existing installation is kept until the
			// complete copy has been validated.
			string staging = gameDir + ".importing";
			TryDeleteDirectory(staging);
			if (Directory.Exists(staging))
				throw new InstallException("Couldn't clear an unfinished import. Check free storage and try again.");

			try
			{
				for (int i = 0; i < files.Count; i++)
				{
					var (relative, size, open) = files[i];
					string dest = SafeDestination(staging, relative);
					Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
					using (Stream input = open())
					using (FileStream output = File.Create(dest))
					{
						int read;
						while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
						{
							output.Write(buffer, 0, read);
							done += read;
						}
					}
					float fraction = size >= 0 ? done / (float)total : (i + 1) / (float)files.Count;
					if (i % 8 == 0 || i == files.Count - 1)
						progress($"Copying game files… {i + 1}/{files.Count}", fraction);
				}

				bool hasContent = Directory.Exists(Path.Combine(staging, "Content"))
					|| Directory.EnumerateDirectories(staging).Any(path => Path.GetFileName(path).Equals("Content", StringComparison.OrdinalIgnoreCase));
				bool hasExe = File.Exists(Path.Combine(staging, "Celeste.exe"))
					|| Directory.EnumerateFiles(staging).Any(path => Path.GetFileName(path).Equals("Celeste.exe", StringComparison.OrdinalIgnoreCase));
				if (!hasExe || !hasContent)
					throw new InstallException("The copy is incomplete.");

				string previous = gameDir + ".previous";
				TryDeleteDirectory(previous);
				if (Directory.Exists(previous))
					throw new InstallException("Couldn't clear the previous game copy.");
				bool movedPrevious = false;
				bool movedNew = false;
				try
				{
					if (Directory.Exists(gameDir))
					{
						Directory.Move(gameDir, previous);
						movedPrevious = true;
					}
					Directory.Move(staging, gameDir);
					movedNew = true;
					TryDeleteDirectory(previous);
				}
				catch
				{
					if (movedNew)
						TryDeleteDirectory(gameDir);
					if (movedPrevious && !Directory.Exists(gameDir))
						Directory.Move(previous, gameDir);
					throw;
				}
			}
			catch
			{
				// Do not leave a partial multi-gigabyte copy behind after an error.
				TryDeleteDirectory(staging);
				throw;
			}
		}

		#endregion

		#region Espaço em disco

		/// <summary>
		/// Confere se a nova cópia cabe antes de começar: o jogo instalado só é substituído no
		/// fim, então o pico é (cópia atual + cópia nova) — melhor avisar agora do que falhar no
		/// meio de 1 GB de cópia.
		/// </summary>
		private void EnsureFreeSpace(long? sourceBytes)
		{
			if (sourceBytes == null)
				return;
			long? available = AvailableBytes(Files(context));
			if (available == null)
				return;

			long installed = DirectoryBytes(GameDir(context));
			long working = gameDir.Equals(GameDir(context), StringComparison.Ordinal) ? 0 : DirectoryBytes(gameDir);
			long needed = sourceBytes.Value + installed + working + MarginBytes;
			if (available.Value >= needed)
				return;

			// Sobra de uma importação interrompida é o primeiro lugar onde procurar espaço.
			string staging = gameDir + ".importing";
			if (Directory.Exists(staging))
			{
				Log.Info(GameActivity.LogTag, "Removing leftover import data to free space…");
				TryDeleteDirectory(staging);
				available = AvailableBytes(Files(context)) ?? available;
				installed = DirectoryBytes(GameDir(context));
				working = gameDir.Equals(GameDir(context), StringComparison.Ordinal) ? 0 : DirectoryBytes(gameDir);
				needed = sourceBytes.Value + installed + working + MarginBytes;
				if (available.Value >= needed)
					return;
			}

			throw new InstallException(
				$"Not enough free space: about {Size(needed)} needed " +
				$"({Size(sourceBytes.Value)} for the new data" +
				(installed > 0 ? $" + {Size(installed)} for the current install" : "") +
				(working > 0 ? $" + {Size(working)} for the staged copy" : "") +
				$", plus {Size(MarginBytes)} for Everest and patching), but only {Size(available.Value)} is free. " +
				"Free up some space and try again."
			);
		}

		/// <summary>Espaço livre no volume do armazenamento interno (null se o Android não informar).</summary>
		private static long? AvailableBytes(string path)
		{
			try
			{
				return new StatFs(path).AvailableBytes;
			}
			catch (Exception e)
			{
				Log.Warn(GameActivity.LogTag, "Não deu para consultar o espaço livre: " + e.Message);
				return null;
			}
		}

		private static long DirectoryBytes(string path)
		{
			if (!Directory.Exists(path))
				return 0;
			long total = 0;
			try
			{
				foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
					total += new FileInfo(file).Length;
			}
			catch (IOException)
			{
				// Medida parcial serve: é só para avisar antes de encher o aparelho.
			}
			return total;
		}

		/// <summary>O APK pessoal é praticamente o jogo embutido: estimativa conservadora.</summary>
		private long? ApkBytes()
		{
			try
			{
				string? apk = context.ApplicationInfo!.SourceDir;
				return apk == null ? null : new Java.IO.File(apk).Length();
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static string Size(long bytes) =>
			bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GB" : $"{bytes / (1L << 20)} MB";

		private static void TryDeleteDirectory(string path)
		{
			try
			{
				if (Directory.Exists(path))
					Directory.Delete(path, recursive: true);
			}
			catch (Exception e)
			{
				Log.Warn(GameActivity.LogTag, $"Não deu para apagar {path}: {e.Message}");
			}
		}

		#endregion

		#region Patch e fundo

		/// <summary>Gera o Celeste.dll final. Everest recebe como entrada a saída preservada do MiniInstaller.</summary>
		public void Patch() => Patch(IsEverestInstalled(context));

		public void Patch(bool useEverest)
		{
			progress("Applying CelesteAndroid patches…", -1);
			string patcherDir = ExtractPatcherAssets();
			string input = Path.Combine(gameDir, useEverest ? "Celeste.dll" : "Celeste.exe");
			if (!File.Exists(input))
				throw new InstallException(useEverest
					? "Everest's intermediate Celeste.dll is missing; reinstall the game with Everest enabled."
					: "Celeste.exe is missing from the imported game.");

			if (useEverest)
			{
				string androidSteamworks = Path.Combine(patcherDir, "Steamworks.NET.dll");
				if (File.Exists(androidSteamworks))
					File.Copy(androidSteamworks, Path.Combine(gameDir, "Steamworks.NET.dll"), overwrite: true);
			}

			Directory.CreateDirectory(patchedDir);
			string patched = Path.Combine(patchedDir, "Celeste.dll");
			string staging = patched + ".tmp";
			string[] dependencies = useEverest
				? new[] { gameDir, Path.Combine(gameDir, "everest-lib"), patcherDir }
				: new[] { patcherDir };
			CelestePatcher.Patch(
				input,
				Path.Combine(patcherDir, "Celeste.Android.mm.dll"),
				staging,
				dependencies,
				msg => Log.Info(GameActivity.LogTag, msg)
			);
			File.Move(staging, patched, overwrite: true);
			foreach (string leftover in Directory.GetFiles(patchedDir, "*.mdb"))
				File.Delete(leftover);

			string stamp = HashText(
				$"v2|{(useEverest ? "everest" : "vanilla")}|" +
				$"{HashFile(Path.Combine(patcherDir, PatcherAssetName))}|{HashFile(input)}");
			File.WriteAllText(Path.Combine(patchedDir, "patch.stamp"), stamp);
		}

		private string ExtractPatcherAssets()
		{
			string patcherDir = Path.Combine(context.CacheDir!.AbsolutePath, "patcher");
			TryDeleteDirectory(patcherDir);
			if (Directory.Exists(patcherDir))
				throw new InstallException("Couldn't clear stale patcher files from app cache.");
			Directory.CreateDirectory(patcherDir);
			string[] assets = context.Assets!.List("patcher") ?? Array.Empty<string>();
			if (assets.Length == 0)
				throw new InstallException("The APK doesn't contain the Android patcher files.");
			foreach (string asset in assets)
			{
				using Stream input = context.Assets.Open("patcher/" + asset);
				using FileStream output = File.Create(Path.Combine(patcherDir, asset));
				input.CopyTo(output);
			}
			return patcherDir;
		}

		/// <summary>Versão desfocada e escurecida da key art do jogo, para as faixas laterais.</summary>
		public void PrepareBackground()
		{
			string art = Path.Combine(gameDir, "Content", "Graphics", "SplashScreen.png");
			if (!File.Exists(art))
				return;
			progress("Generating the background…", -1);

			const int width = 192, height = 108;
			using Bitmap decoded = BitmapFactory.DecodeFile(art, new BitmapFactory.Options { InSampleSize = 8 })!;
			using Bitmap small = Bitmap.CreateScaledBitmap(decoded, width, height, true)!;
			int[] pixels = new int[width * height];
			small.GetPixels(pixels, 0, width, 0, 0, width, height);

			// Três passadas de box blur ≈ desfoque gaussiano; depois escurece para não competir com o jogo.
			for (int pass = 0; pass < 3; pass++)
			{
				BoxBlur(pixels, width, height, radius: 7, horizontal: true);
				BoxBlur(pixels, width, height, radius: 7, horizontal: false);
			}
			for (int i = 0; i < pixels.Length; i++)
			{
				int p = pixels[i];
				pixels[i] = unchecked((int)0xFF000000)
					| (((p >> 16) & 0xFF) * 38 / 100) << 16
					| (((p >> 8) & 0xFF) * 38 / 100) << 8
					| ((p & 0xFF) * 38 / 100);
			}

			using Bitmap result = Bitmap.CreateBitmap(pixels, width, height, Bitmap.Config.Argb8888!)!;
			using FileStream output = File.Create(backgroundPath);
			result.Compress(Bitmap.CompressFormat.Png!, 100, output);
		}

		private static void BoxBlur(int[] pixels, int width, int height, int radius, bool horizontal)
		{
			int lines = horizontal ? height : width;
			int length = horizontal ? width : height;
			int[] line = new int[length];
			for (int l = 0; l < lines; l++)
			{
				int Index(int i) => horizontal ? l * width + i : i * width + l;
				for (int i = 0; i < length; i++)
					line[i] = pixels[Index(i)];

				for (int i = 0; i < length; i++)
				{
					int r = 0, g = 0, b = 0, count = 0;
					for (int k = Math.Max(0, i - radius); k <= Math.Min(length - 1, i + radius); k++)
					{
						int p = line[k];
						r += (p >> 16) & 0xFF;
						g += (p >> 8) & 0xFF;
						b += p & 0xFF;
						count++;
					}
					pixels[Index(i)] = unchecked((int)0xFF000000) | (r / count) << 16 | (g / count) << 8 | (b / count);
				}
			}
		}

		#endregion

		#region SAF / assets

		private readonly record struct Doc(string Id, string Name, bool IsDir, long Size);

		private List<Doc> ListChildren(Uri treeUri, string parentId)
		{
			var result = new List<Doc>();
			Uri children = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, parentId)!;
			string[] projection =
			{
				DocumentsContract.Document.ColumnDocumentId,
				DocumentsContract.Document.ColumnDisplayName,
				DocumentsContract.Document.ColumnMimeType,
				DocumentsContract.Document.ColumnSize,
			};
			using ICursor? cursor = context.ContentResolver!.Query(children, projection, null, null, null);
			while (cursor != null && cursor.MoveToNext())
			{
				result.Add(new Doc(
					cursor.GetString(0)!,
					cursor.GetString(1) ?? "",
					cursor.GetString(2) == DocumentsContract.Document.MimeTypeDir,
					cursor.IsNull(3) ? -1 : cursor.GetLong(3)
				));
			}
			return result;
		}

		/// <summary>Aceita a própria pasta do jogo ou uma pasta acima dela (ex.: "common" da Steam).</summary>
		private Doc? FindGameRoot(Uri treeUri, Doc dir, int depth)
		{
			List<Doc> children = ListChildren(treeUri, dir.Id);
			if (children.Any(c => c.Name.Equals("Celeste.exe", StringComparison.OrdinalIgnoreCase))
				&& children.Any(c => c.Name.Equals("Content", StringComparison.OrdinalIgnoreCase) && c.IsDir))
				return dir;
			if (depth == 0)
				return null;
			foreach (Doc child in children.Where(c => c.IsDir))
			{
				Doc? found = FindGameRoot(treeUri, child, depth - 1);
				if (found != null)
					return found;
			}
			return null;
		}

		private void CollectFiles(Uri treeUri, Doc dir, string relative, List<(Doc, string)> files)
		{
			foreach (Doc child in ListChildren(treeUri, dir.Id))
			{
				string path = relative + "/" + child.Name;
				if (child.IsDir)
					CollectFiles(treeUri, child, path, files);
				else
					files.Add((child, path));
			}
		}

		private void CollectAssets(string path, List<string> files)
		{
			string[] children = context.Assets!.List(path) ?? Array.Empty<string>();
			if (children.Length == 0)
			{
				files.Add(path);
				return;
			}
			foreach (string child in children)
				CollectAssets(path + "/" + child, files);
		}

		#endregion
	}

	public sealed class InstallException(string message) : Exception(message);
}
