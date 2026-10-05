using Android.Content;

namespace CelesteAndroid
{
	/// <summary>
	/// Preferências do launcher. Ficam no mesmo arquivo usado pelo host do jogo
	/// (CelesteLauncher), por isso as chaves estão aqui em um lugar só.
	/// </summary>
	internal static class LauncherPrefs
	{
		public const string File = "launcher";

		/// <summary>Driver gráfico forçado ("OpenGL" para OpenGL ES; vazio = Vulkan).</summary>
		public const string Driver = "driver";

		/// <summary>Controles na tela ("1"/"0"). Ligados por padrão.</summary>
		public const string Touch = "touch";

		private static ISharedPreferences Get(Context context) =>
			context.GetSharedPreferences(File, FileCreationMode.Private)!;

		public static string? Driver(Context context) => Get(context).GetString(Driver, "");

		public static void SetDriver(Context context, string driver) =>
			Get(context).Edit()!.PutString(Driver, driver)!.Apply();

		/// <summary>Os controles de toque vêm ligados: sem eles o jogo não tem como ser jogado
		/// no celular sem um controle conectado.</summary>
		public static bool TouchControls(Context context) => Get(context).GetString(Touch, "1") != "0";

		public static void SetTouchControls(Context context, bool enabled) =>
			Get(context).Edit()!.PutString(Touch, enabled ? "1" : "0")!.Apply();
	}
}
