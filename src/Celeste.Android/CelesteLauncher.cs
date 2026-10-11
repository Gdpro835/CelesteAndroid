using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Android.Content;
using Android.Util;

namespace CelesteAndroid
{
	/// <summary>
	/// Carrega o Celeste.dll patcheado e chama o Main original, com o mesmo contrato do host desktop.
	/// </summary>
	public static class CelesteLauncher
	{
		private static readonly object resolverLock = new();
		private static string[] everestAssemblyDirs = Array.Empty<string>();
		private static bool everestResolverInstalled;
		private static readonly string[] everestSystemAssemblies =
		{
			"System.Drawing.Common", "System.Security.Permissions", "System.Windows.Extensions",
		};

		/// <summary>
		/// Called by GameActivity before the first FNA access. In Everest mode the MiniInstaller-patched
		/// FNA lives beside the game and must be loaded before the APK's assembly-store copy.
		/// </summary>
		public static void PreloadEverestRuntime(Context context)
		{
			string gameDir = GameInstaller.GameDir(context);
			string fnaPath = Path.Combine(gameDir, "FNA.dll");
			if (!File.Exists(fnaPath))
				throw new FileNotFoundException("Everest installation is missing its Android-patched FNA.dll.", fnaPath);

			lock (resolverLock)
			{
				if (!everestResolverInstalled)
				{
					everestAssemblyDirs = new[] { gameDir, Path.Combine(gameDir, "everest-lib") };
					AssemblyLoadContext.Default.Resolving += ResolveEverestAssembly;
					everestResolverInstalled = true;
				}
			}

			Assembly? alreadyLoaded = null;
			foreach (Assembly assembly in AssemblyLoadContext.Default.Assemblies)
			{
				if (assembly.GetName().Name == "FNA")
				{
					alreadyLoaded = assembly;
					break;
				}
			}
			if (alreadyLoaded != null)
			{
				if (string.Equals(alreadyLoaded.Location, fnaPath, StringComparison.Ordinal))
					return;
				throw new InvalidOperationException(
					"FNA was loaded from the APK before Everest's patched FNA. Restart the app; if this repeats, the patched FNA cannot be selected by this runtime.");
			}

			AssemblyLoadContext.Default.LoadFromAssemblyPath(fnaPath);
			Log.Info(GameActivity.LogTag, "Preloaded Android FNA patched by Everest MiniInstaller.");
		}

		private static Assembly? ResolveEverestAssembly(AssemblyLoadContext context, AssemblyName name)
		{
			if (string.IsNullOrEmpty(name.Name) || IsFrameworkAssembly(name.Name))
				return null;
			foreach (string directory in everestAssemblyDirs)
			{
				string candidate = Path.Combine(directory, name.Name + ".dll");
				if (File.Exists(candidate))
					return context.LoadFromAssemblyPath(candidate);
			}
			return null;
		}

		private static bool IsFrameworkAssembly(string name)
		{
			if (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase))
				return !Array.Exists(everestSystemAssemblies, special => special.Equals(name, StringComparison.OrdinalIgnoreCase));
			return name.Equals("System", StringComparison.OrdinalIgnoreCase)
				|| name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase)
				|| name.Equals("netstandard", StringComparison.OrdinalIgnoreCase)
				|| name.Equals("Microsoft.CSharp", StringComparison.OrdinalIgnoreCase)
				|| name.Equals("Mono.Posix", StringComparison.OrdinalIgnoreCase)
				|| name.Equals("Mono.Security", StringComparison.OrdinalIgnoreCase)
				|| name.StartsWith("Microsoft.NETCore.", StringComparison.OrdinalIgnoreCase);
		}

		public static void Run(Context context)
		{
			string gameDir = GameInstaller.GameDir(context);
			Log.Info(GameActivity.LogTag, $"Iniciando Celeste de {gameDir}");

			// Ver CelesteAndroid.HostConfig (compilado de src/Shared/HostConfig.cs).
			bool touch = LauncherPrefs.TouchControls(context);
			HostConfig.Publish(
				"Android",
				GameInstaller.UserDir(context),
				GameInstaller.BackgroundPng(context),
				touch,
				LauncherPrefs.TouchLayout(context)
			);
			Log.Info(GameActivity.LogTag, $"Controles de toque: {(touch ? "ligados" : "desligados")}");

			// O FNA resolve o Content relativo ao diretório de trabalho no Android.
			AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", gameDir + Path.DirectorySeparatorChar);
			Environment.CurrentDirectory = gameDir;

			Assembly celeste = AssemblyLoadContext.Default.LoadFromAssemblyPath(GameInstaller.PatchedDll(context));

			// Engine.AssemblyDirectory vem de Assembly.Location, que não aponta para o jogo.
			celeste.GetType("Monocle.Engine", throwOnError: true)!
				.GetField("AssemblyDirectory", BindingFlags.NonPublic | BindingFlags.Static)!
				.SetValue(null, gameDir);

			MethodInfo main = celeste.GetType("Celeste.Celeste", throwOnError: true)!
				.GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static)!;
			main.Invoke(null, new object[] { Array.Empty<string>() });
		}
	}
}
