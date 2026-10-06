using System;

namespace CelesteAndroid.Touch
{
	/// <summary>
	/// The keys the on-screen pad presses. They are the keys Celeste binds by default:
	/// arrows to move, Z (grab), X (dash), C (jump), Escape (pause), Tab (journal) and R (retry).
	/// </summary>
	internal enum GameKey
	{
		C,
		X,
		Z,
		Escape,
		Tab,
		R,
		Left,
		Right,
		Up,
		Down,
	}

	internal static class GameKeyInfo
	{
		/// <summary>
		/// Physical (scancode) and virtual (keycode) codes, taken from SDL_scancode.h and
		/// SDL_keycode.h of the SDL 3.4.16 the port builds against.
		/// </summary>
		public static (int Scancode, uint Keycode) Codes(this GameKey key) => key switch
		{
			GameKey.C => (6, 0x63u),          // SDL_SCANCODE_C / SDLK_C
			GameKey.X => (27, 0x78u),         // SDL_SCANCODE_X / SDLK_X
			GameKey.Z => (29, 0x7Au),         // SDL_SCANCODE_Z / SDLK_Z
			GameKey.Escape => (41, 0x1Bu),    // SDL_SCANCODE_ESCAPE / SDLK_ESCAPE
			GameKey.Tab => (43, 0x09u),       // SDL_SCANCODE_TAB / SDLK_TAB
			GameKey.R => (21, 0x72u),         // SDL_SCANCODE_R / SDLK_R
			GameKey.Left => (80, 0x40000050u),  // SDL_SCANCODE_LEFT / SDLK_LEFT
			GameKey.Right => (79, 0x4000004Fu), // SDL_SCANCODE_RIGHT / SDLK_RIGHT
			GameKey.Up => (82, 0x40000052u),    // SDL_SCANCODE_UP / SDLK_UP
			GameKey.Down => (81, 0x40000051u),  // SDL_SCANCODE_DOWN / SDLK_DOWN
			_ => throw new ArgumentOutOfRangeException(nameof(key)),
		};

		public static string Label(this GameKey key) => key switch
		{
			GameKey.C => "C (jump)",
			GameKey.X => "X (dash)",
			GameKey.Z => "Z (grab)",
			GameKey.Escape => "Escape (pause)",
			GameKey.Tab => "Tab (journal)",
			GameKey.R => "R (retry)",
			_ => key + " (move)",
		};
	}
}
