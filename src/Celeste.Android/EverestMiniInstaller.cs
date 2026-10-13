using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Android.Util;

namespace CelesteAndroid
{
	/// <summary>
	/// Android adapter for the managed part of MiniInstaller in main.zip. It avoids Program.Main,
	/// which refuses Mono and then configures desktop apphosts/native libraries; this adapter invokes
	/// the same NETCoreifier, MonoMod and HookGen helpers without desktop-specific post-install steps.
	/// </summary>
	internal static class EverestMiniInstaller
	{
		private static readonly string[] DesktopNativeLibraries =
		{
			"fmod.dll", "fmodstudio.dll", "CSteamworks.dll", "steam_api.dll", "FNA3D.dll", "SDL2.dll",
		};

		private static readonly string[] ToolFiles =
		{
			"Mono.Cecil.dll",
			"Mono.Cecil.Mdb.dll",
			"Mono.Cecil.Pdb.dll",
			"MonoMod.Utils.dll",
			"MonoMod.Patcher.dll",
			"MonoMod.RuntimeDetour.dll",
			"MonoMod.RuntimeDetour.HookGen.dll",
			"NETCoreifier.dll",
		};

		public static void Install(
			string gameDir,
			string androidFnaPath,
			string frameworkReferenceDir,
			string workspace,
			string tempDir,
			Action<string, float> progress)
		{
			string previousDirectory = Environment.CurrentDirectory;
			try
			{
				Environment.CurrentDirectory = gameDir;
				InstallCore(gameDir, androidFnaPath, frameworkReferenceDir, workspace, tempDir, progress);
			}
			finally
			{
				Environment.CurrentDirectory = previousDirectory;
				Environment.SetEnvironmentVariable("MONOMOD_DEPDIRS", "");
				Environment.SetEnvironmentVariable("MONOMOD_MODS", "");
				Environment.SetEnvironmentVariable("MONOMOD_DEPENDENCY_MISSING_THROW", "");
			}
		}

		private static void InstallCore(
			string gameDir,
			string androidFnaPath,
			string frameworkReferenceDir,
			string workspace,
			string tempDir,
			Action<string, float> progress)
		{
			string everestLib = Path.Combine(gameDir, "everest-lib");
			string miniInstallerPath = Path.Combine(gameDir, "MiniInstaller.dll");
			string celesteMod = Path.Combine(gameDir, "Celeste.Mod.mm.dll");
			string originalDir = Path.Combine(gameDir, "orig");
			string celesteExe = Path.Combine(gameDir, "Celeste.exe");

			if (!File.Exists(celesteExe) || !File.Exists(celesteMod) || !Directory.Exists(everestLib))
				throw new InstallException("This main.zip is incomplete: Celeste.Mod.mm.dll or everest-lib is missing.");
			if (!File.Exists(androidFnaPath))
				throw new InstallException("The Android FNA assembly needed to prepare Everest is missing from the APK.");
			if (!File.Exists(Path.Combine(frameworkReferenceDir, "mscorlib.dll")))
				throw new InstallException("The APK is missing the mscorlib reference assembly needed to convert the original game for Everest.");

			// Match Program.Main's normal (non-headless) mode for MonoModRules without entering its desktop setup path.
			AppDomain.CurrentDomain.SetData("Everest_IsHeadless", false);
			Directory.CreateDirectory(originalDir);
			if (!File.Exists(Path.Combine(originalDir, "Celeste.exe")))
				File.Copy(celesteExe, Path.Combine(originalDir, "Celeste.exe"));
			foreach (string dependency in Directory.EnumerateFiles(gameDir, "*.dll", SearchOption.TopDirectoryOnly))
			{
				string backup = Path.Combine(originalDir, Path.GetFileName(dependency));
				if (!File.Exists(backup))
					File.Copy(dependency, backup);
			}
			foreach (string config in Directory.EnumerateFiles(gameDir, "*.config", SearchOption.TopDirectoryOnly))
			{
				string backup = Path.Combine(originalDir, Path.GetFileName(config));
				if (!File.Exists(backup))
					File.Copy(config, backup);
			}

			// MiniInstaller applies lib-vanilla to its original-game backup before NETCoreifier
			// runs. These are compatibility replacements for original dependencies, not runtime mods.
			string vanillaLibraries = Path.Combine(everestLib, "lib-vanilla");
			if (Directory.Exists(vanillaLibraries))
			{
				CopyDirectory(vanillaLibraries, originalDir, overwrite: true);
				Directory.Delete(vanillaLibraries, recursive: true);
			}

			// Match MiniInstaller's DeleteSystemLibs step after preserving the originals above.
			foreach (string file in Directory.EnumerateFiles(gameDir, "*.dll", SearchOption.TopDirectoryOnly))
			{
				if (IsSystemLibrary(file))
					File.Delete(file);
			}
			// The Android app supplies native FNA3D/FAudio/SDL3/FMOD libraries from the APK, not these PC DLLs.
			foreach (string name in DesktopNativeLibraries)
			{
				string path = Path.Combine(gameDir, name);
				if (File.Exists(path))
					File.Delete(path);
			}

			// NETCoreifier's Cecil resolver runs on Android, where SDK facades aren't exposed as
			// physical files. Supply mscorlib and its System.* facade dependency closure in both
			// search roots before converting the original .NET Framework executable.
			InstallFrameworkResolverReferences(gameDir, originalDir, frameworkReferenceDir);

			// Normalize tool DLLs into the game root (some main.zip builds place them under everest-lib).
			// The isolated load context below loads the same files that upstream DepCalls expects in PathGame.
			foreach (string name in ToolFiles)
			{
				string rootPath = Path.Combine(gameDir, name);
				string libPath = Path.Combine(everestLib, name);
				if (!File.Exists(rootPath) && File.Exists(libPath))
					File.Copy(libPath, rootPath);
			}
			foreach (string required in ToolFiles)
			{
				if (!File.Exists(Path.Combine(gameDir, required)))
					throw new InstallException($"Everest MiniInstaller is missing {required} in main.zip.");
			}
			if (!File.Exists(miniInstallerPath))
				throw new InstallException("Everest main.zip is missing MiniInstaller.dll.");

			if (Directory.Exists(workspace))
				Directory.Delete(workspace, recursive: true);
			if (Directory.Exists(tempDir))
				Directory.Delete(tempDir, recursive: true);
			Directory.CreateDirectory(workspace);
			Directory.CreateDirectory(tempDir);

			var loadContext = new EverestToolLoadContext(
				"EverestMiniInstaller-" + Guid.NewGuid().ToString("N"),
				new[] { gameDir, everestLib });
			try
			{
				using (loadContext.EnterContextualReflection())
				{
					Assembly miniInstaller = loadContext.LoadFromAssemblyPath(miniInstallerPath);
					Type globals = miniInstaller.GetType("MiniInstaller.Globals", throwOnError: true)!;
					SetGlobal(globals, "PathGame", gameDir);
					SetGlobal(globals, "PathUpdate", null);
					SetGlobal(globals, "PathOSXExecDir", null);
					SetGlobal(globals, "PathCelesteExe", celesteExe);
					SetGlobal(globals, "PathEverestDLL", Path.Combine(gameDir, "Celeste.dll"));
					SetGlobal(globals, "PathEverestLib", everestLib);
					SetGlobal(globals, "PathOrig", originalDir);
					SetGlobal(globals, "PathLog", Path.Combine(tempDir, "miniinstaller-log.txt"));
					SetGlobal(globals, "PathTmp", tempDir);
					SetGlobal(globals, "PathMiniInstallerWorkspace", workspace);

					Type depCalls = miniInstaller.GetType("MiniInstaller.DepCalls", throwOnError: true)!;
					LoadMiniInstallerTools(depCalls, loadContext, gameDir);

					string originalCeleste = Path.Combine(originalDir, "Celeste.exe");
					string moddedCeleste = Path.Combine(workspace, "Celeste.dll");
					string moddedFna = Path.Combine(workspace, "FNA.dll");
					string hookGenOutput = Path.Combine(workspace, "MMHOOK_Celeste.dll");
					string[] mods = { everestLib, celesteMod };

					progress("Everest: converting the original game…", -1);
					Invoke(depCalls, "ConvertToNETCore", new object?[] { originalCeleste, moddedCeleste, null });
					// The Android app's FNA is already a net8 assembly. Keep it unchanged before Everest's
					// FNA patches instead of running the desktop-oriented NETCoreifier over it.
					File.Copy(androidFnaPath, moddedFna, overwrite: true);

					progress("Everest: patching FNA and Celeste…", -1);
					Invoke(depCalls, "RunMonoMod", new object?[] { moddedFna, moddedFna, mods });
					Invoke(depCalls, "RunMonoMod", new object?[] { moddedCeleste, moddedCeleste, mods });

					progress("Everest: generating mod hooks…", -1);
					Invoke(depCalls, "RunHookGen", new object?[] { moddedCeleste, moddedCeleste });
					if (!File.Exists(hookGenOutput))
						throw new InstallException("Everest HookGen did not produce MMHOOK_Celeste.dll.");
					Invoke(depCalls, "RunMonoMod", new object?[] { hookGenOutput, hookGenOutput, mods });

					if (!File.Exists(moddedCeleste) || !File.Exists(moddedFna))
						throw new InstallException("Everest MiniInstaller did not produce the patched game and FNA assemblies.");

					File.Copy(moddedCeleste, Path.Combine(gameDir, "Celeste.dll"), overwrite: true);
					File.Copy(moddedFna, Path.Combine(gameDir, "FNA.dll"), overwrite: true);
					File.Copy(hookGenOutput, Path.Combine(gameDir, "MMHOOK_Celeste.dll"), overwrite: true);
					Directory.CreateDirectory(Path.Combine(gameDir, "Mods"));

					string controllerDb = Path.Combine(everestLib, "gamecontrollerdb.txt");
					if (File.Exists(controllerDb))
						File.Copy(controllerDb, Path.Combine(gameDir, "gamecontrollerdb.txt"), overwrite: true);

					Log.Info(GameActivity.LogTag, "Everest MiniInstaller managed stages completed; desktop apphost setup was skipped.");
					progress("Everest installation prepared for Android.", 1f);
				}
			}
			finally
			{
				loadContext.Unload();
				TryDeleteDirectory(workspace);
				TryDeleteDirectory(tempDir);
			}
		}

		private static void InstallFrameworkResolverReferences(string gameDir, string originalDir, string referenceDir)
		{
			string mscorlib = Path.Combine(referenceDir, "mscorlib.dll");
			if (!File.Exists(mscorlib))
				throw new InstallException("The APK is missing the mscorlib reference assembly needed to convert the original game for Everest.");

			foreach (string source in Directory.EnumerateFiles(referenceDir, "*.dll", SearchOption.TopDirectoryOnly))
			{
				string name = Path.GetFileName(source);
				if (!IsFrameworkResolverReference(name))
					continue;

				File.Copy(source, Path.Combine(gameDir, name), overwrite: true);
				string originalReference = Path.Combine(originalDir, name);
				if (!File.Exists(originalReference))
					File.Copy(source, originalReference);
			}
		}

		private static bool IsFrameworkResolverReference(string name)
		{
			return name.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase)
				|| (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) && IsSystemLibrary(name));
		}

		private static void TryDeleteDirectory(string path)
		{
			try
			{
				if (Directory.Exists(path))
					Directory.Delete(path, recursive: true);
			}
			catch (Exception e)
			{
				Log.Warn(GameActivity.LogTag, $"Couldn't remove Everest installer temp data {path}: {e.Message}");
			}
		}

		private static void CopyDirectory(string source, string destination, bool overwrite)
		{
			Directory.CreateDirectory(destination);
			foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
				Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
			foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
			{
				string target = Path.Combine(destination, Path.GetRelativePath(source, file));
				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				File.Copy(file, target, overwrite);
			}
		}

		private static bool IsSystemLibrary(string path)
		{
			string name = Path.GetFileName(path);
			if (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
				&& !name.Equals("System.Drawing.Common.dll", StringComparison.OrdinalIgnoreCase)
				&& !name.Equals("System.Security.Permissions.dll", StringComparison.OrdinalIgnoreCase)
				&& !name.Equals("System.Windows.Extensions.dll", StringComparison.OrdinalIgnoreCase))
				return true;
			return name.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase)
				|| name.Equals("Mono.Posix.dll", StringComparison.OrdinalIgnoreCase)
				|| name.Equals("Mono.Security.dll", StringComparison.OrdinalIgnoreCase);
		}

		private static void LoadMiniInstallerTools(Type depCalls, AssemblyLoadContext loadContext, string gameDir)
		{
			var loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
			foreach (string name in ToolFiles)
			{
				string path = Path.Combine(gameDir, name);
				if (!File.Exists(path))
					throw new InstallException($"Everest MiniInstaller is missing {name} in main.zip.");
				loaded[name] = loadContext.LoadFromAssemblyPath(path);
			}

			SetAssemblyField(depCalls, "AsmMonoMod", loaded["MonoMod.Patcher.dll"]);
			SetAssemblyField(depCalls, "AsmHookGen", loaded["MonoMod.RuntimeDetour.HookGen.dll"]);
			SetAssemblyField(depCalls, "AsmNETCoreifier", loaded["NETCoreifier.dll"]);
		}

		private static void SetAssemblyField(Type type, string name, Assembly assembly)
		{
			FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Static)
				?? throw new InstallException($"Everest MiniInstaller is missing {type.FullName}.{name}.");
			field.SetValue(null, assembly);
		}

		private static void SetGlobal(Type globals, string name, object? value)
		{
			FieldInfo field = globals.GetField(name, BindingFlags.Public | BindingFlags.Static)
				?? throw new InstallException($"Everest MiniInstaller is missing Globals.{name}.");
			field.SetValue(null, value);
		}

		private static void Invoke(Type type, string name, object?[] arguments)
		{
			MethodInfo method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static)
				?? throw new InstallException($"Everest MiniInstaller is missing {type.FullName}.{name}().");
			try
			{
				method.Invoke(null, arguments);
			}
			catch (TargetInvocationException e) when (e.InnerException != null)
			{
				Exception cause = e;
				while (cause is TargetInvocationException { InnerException: not null } invocation)
					cause = invocation.InnerException;
				Log.Error(GameActivity.LogTag, $"Everest MiniInstaller failed in {name}: {e}");
				string stack = FirstFailureFrames(cause);
				throw new InstallException(
					$"Everest MiniInstaller failed in {name}: {cause.GetType().Name}: {cause.Message}{stack}");
			}
		}

		private static string FirstFailureFrames(Exception exception)
		{
			string? stackTrace = exception.StackTrace;
			if (string.IsNullOrEmpty(stackTrace))
				return string.Empty;

			// Include the innermost runtime/Cecil frames too: NETCoreifier's outer overloads alone
			// don't identify which reflection or metadata operation failed.
			var frames = new List<string>(5);
			foreach (string line in stackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
			{
				string frame = line.Trim();
				if (!frame.StartsWith("at ", StringComparison.Ordinal))
					continue;

				frames.Add(frame);
				if (frames.Count == 5)
					break;
			}

			return frames.Count == 0 ? string.Empty : "\n" + string.Join("\n", frames);
		}

		private static bool IsFrameworkAssembly(string name)
		{
			if (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase))
				return !name.Equals("System.Drawing.Common", StringComparison.OrdinalIgnoreCase)
					&& !name.Equals("System.Security.Permissions", StringComparison.OrdinalIgnoreCase)
					&& !name.Equals("System.Windows.Extensions", StringComparison.OrdinalIgnoreCase);
			return name.Equals("System", StringComparison.OrdinalIgnoreCase)
				|| name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase)
				|| name.Equals("netstandard", StringComparison.OrdinalIgnoreCase)
				|| name.Equals("Microsoft.CSharp", StringComparison.OrdinalIgnoreCase)
				|| name.Equals("Mono.Posix", StringComparison.OrdinalIgnoreCase)
				|| name.Equals("Mono.Security", StringComparison.OrdinalIgnoreCase)
				|| name.StartsWith("Microsoft.NETCore.", StringComparison.OrdinalIgnoreCase);
		}

		private sealed class EverestToolLoadContext(string name, string[] directories) : AssemblyLoadContext(name, isCollectible: true)
		{
			protected override Assembly? Load(AssemblyName assemblyName)
			{
				if (string.IsNullOrEmpty(assemblyName.Name) || IsFrameworkAssembly(assemblyName.Name))
					return null;

				foreach (string directory in directories)
				{
					string candidate = Path.Combine(directory, assemblyName.Name + ".dll");
					if (File.Exists(candidate))
						return LoadFromAssemblyPath(candidate);
				}
				return null;
			}
		}
	}
}
