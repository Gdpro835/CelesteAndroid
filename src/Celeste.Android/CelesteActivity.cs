using System;
using System.Reflection;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using Microsoft.Xna.Framework;
using Org.Libsdl.App;

namespace CelesteAndroid
{
	[Activity(
		Name = "org.celesteandroid.celeste.CelesteActivity",
		Label = "Celeste",
		MainLauncher = true,
		Exported = true,
		Theme = "@android:style/Theme.NoTitleBar.Fullscreen",
		ScreenOrientation = ScreenOrientation.SensorLandscape,
		LaunchMode = LaunchMode.SingleInstance,
		AlwaysRetainTaskState = true,
		HardwareAccelerated = true,
		ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
			| ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation
			| ConfigChanges.UiMode | ConfigChanges.Density | ConfigChanges.SmallestScreenSize)]
	public class CelesteActivity : SDLActivity
	{
		public const string LogTag = "CelesteAndroid";

		// O Java carrega SDL3 e FMOD (o FMOD precisa estar carregado antes do FMOD.init);
		// FNA3D/FAudio são carregados pelo .NET via DllImport.
		protected override string[] GetLibraries() => new[] { "SDL3", "fmod", "fmodstudio" };

		protected override void OnCreate(Bundle? savedInstanceState)
		{
			base.OnCreate(savedInstanceState);
			// O FMOD no Android precisa do Context para o áudio e para ler arquivos.
			Org.Fmod.FMOD.Init(this);
		}

		protected override void OnDestroy()
		{
			Org.Fmod.FMOD.Close();
			base.OnDestroy();
		}

		// Chamado pelo SDL na thread "SDLThread" depois que a superfície existe; substitui o SDL_main nativo.
		protected override void Main()
		{
			// O FNA/FNA3D loga no stderr, que no Android não vai para o logcat.
			FNALoggerEXT.LogInfo = msg => Log.Info(LogTag, msg);
			FNALoggerEXT.LogWarn = msg => Log.Warn(LogTag, msg);
			FNALoggerEXT.LogError = msg => Log.Error(LogTag, msg);

			// Diagnóstico: adb shell am start -n org.celesteandroid.celeste/.CelesteActivity --es driver OpenGL
			string? driver = Intent?.GetStringExtra("driver");
			if (!string.IsNullOrEmpty(driver))
				SDL3.SDL.SDL_SetHint("FNA3D_FORCE_DRIVER", driver);

			try
			{
				if (CelesteLauncher.IsInstalled(this))
				{
					CelesteLauncher.Run(this);
				}
				else
				{
					Log.Warn(LogTag, $"Jogo não encontrado em {CelesteLauncher.GameDir(this)}; rodando HelloGame.");
					using HelloGame game = new();
					game.Run();
				}
			}
			catch (Exception e)
			{
				Log.Error(LogTag, (e is TargetInvocationException { InnerException: not null } tie ? tie.InnerException : e).ToString());
				throw;
			}
		}
	}
}
