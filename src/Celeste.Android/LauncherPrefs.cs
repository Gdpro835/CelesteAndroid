using Android.Content;

namespace CelesteAndroid
{
	/// <summary>
	/// Preferências do launcher. Ficam no mesmo arquivo usado pelo host do jogo
	/// (CelesteLauncher), por isso as chaves estão aqui em um lugar só.
	/// </summary>
	internal static class LauncherPrefs
	{
		// Nomes das chaves com o sufixo "Key": os métodos de leitura são Driver/TouchControls,
		// e um campo com o mesmo nome de um método não compila (CS0102).
		public const string FileName = "launcher";

		/// <summary>Driver gráfico forçado ("OpenGL" para OpenGL ES; vazio = Vulkan).</summary>
		public const string DriverKey = "driver";

		/// <summary>Controles na tela ("1"/"0"). Ligados por padrão.</summary>
		public const string TouchKey = "touch";

		/// <summary>Posição dos controles (ver TouchLayoutSpec.Encode); vazio = padrão.</summary>
		public const string TouchLayoutKey = "touch_layout";

		private static ISharedPreferences Get(Context context) =>
			context.GetSharedPreferences(FileName, FileCreationMode.Private)!;

		public static string? Driver(Context context) => Get(context).GetString(DriverKey, "");

		public static void SetDriver(Context context, string driver) =>
			Get(context).Edit()!.PutString(DriverKey, driver)!.Apply();

		/// <summary>Os controles de toque vêm ligados: sem eles o jogo não tem como ser jogado
		/// no celular sem um controle conectado.</summary>
		public static bool TouchControls(Context context) => Get(context).GetString(TouchKey, "1") != "0";

		public static void SetTouchControls(Context context, bool enabled) =>
			Get(context).Edit()!.PutString(TouchKey, enabled ? "1" : "0")!.Apply();

		public static string TouchLayout(Context context) => Get(context).GetString(TouchLayoutKey, "") ?? "";

		public static void SetTouchLayout(Context context, string layout) =>
			Get(context).Edit()!.PutString(TouchLayoutKey, layout)!.Apply();
	}
}
