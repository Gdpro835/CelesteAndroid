using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Input;

namespace CelesteAndroid.Touch
{
	/// <summary>
	/// Digitação: transforma os caracteres do teclado do sistema em teclas de verdade.
	/// <para>
	/// O jogo (o Celeste, no mínimo) lê somente <c>Keyboard.GetState()</c> — o estado das teclas, uma
	/// vez por quadro. Texto ele não lê: <c>TextInputEXT</c> só serve a mods (é o que o Everest usa).
	/// E o SDL, ao receber um caractere do IME, emite KEY_DOWN e KEY_UP na mesma chamada
	/// (<c>SDL_SendKeyboardUnicodeKey</c>): a tecla fica pressionada e solta dentro do mesmo quadro,
	/// então o jogo nunca vê a digitação. Sem isto, dá para abrir o teclado e ver os caracteres
	/// passando no log, mas nada acontece na tela.
	/// </para>
	/// <para>
	/// Aqui cada caractere vira uma tecla segurada por alguns quadros — tempo suficiente para o
	/// jogo ver a tecla no seu próximo <c>GetState()</c>. Só funciona enquanto o teclado do sistema
	/// está à vista: com um teclado físico as teclas já chegam sozinhas, e repeti-las aqui
	/// duplicaria as batidas.
	/// </para>
	/// </summary>
	internal static class VirtualTyping
	{
		/// <summary>Quantos quadros a tecla fica pressionada (o jogo lê o estado uma vez por quadro).</summary>
		private const int HoldFrames = 2;

		private static readonly Queue<char> pending = new();

		private static int framesLeft;
		private static int heldScancode = -1;
		private static uint heldKeycode;
		private static bool heldShift;

		static VirtualTyping()
		{
			// O caracteres chegam por aqui: o FNA chama isto para cada SDL_EVENT_TEXT_INPUT.
			TextInputEXT.TextInput += OnText;
		}

		public static void Update(bool keyboardShown)
		{
			if (!keyboardShown)
			{
				// Teclado fechado: descarta o que ficou e não repete teclas que o sistema já mandou.
				if (pending.Count > 0)
				{
					pending.Clear();
				}
				if (heldScancode >= 0)
				{
					Release();
				}
				framesLeft = 0;
				return;
			}

			if (framesLeft > 0)
			{
				framesLeft--;
				if (framesLeft == 0)
				{
					Release();
				}
				return;
			}

			while (pending.Count > 0)
			{
				if (Press(pending.Dequeue()))
				{
					framesLeft = HoldFrames;
					return;
				}
				// Caractere sem tecla equivalente (emoji, por exemplo): pula para o próximo.
			}
		}

		private static void OnText(char c)
		{
			if (c != 0)
			{
				pending.Enqueue(c);
			}
		}

		private static bool Press(char c)
		{
			if (!TryMap(c, out int scancode, out uint keycode, out bool shift))
			{
				return false;
			}
			if (shift)
			{
				SdlInput.PushScancode(ScanLeftShift, KeyLeftShift, true);
			}
			SdlInput.PushScancode(scancode, keycode, true);
			heldScancode = scancode;
			heldKeycode = keycode;
			heldShift = shift;
			return true;
		}

		private static void Release()
		{
			SdlInput.PushScancode(heldScancode, heldKeycode, false);
			if (heldShift)
			{
				SdlInput.PushScancode(ScanLeftShift, KeyLeftShift, false);
			}
			heldScancode = -1;
		}

		private const int ScanLeftShift = 225;
		private const uint KeyLeftShift = 0x400000E1u;

		/// <summary>
		/// Caractere → tecla física. Os scancodes são os do SDL (SDL_scancode.h) na disposição
		/// US; o cirílico é traduzido para a tecla que ocupa a mesma posição num teclado russo
		/// (ЙЦУКЕН), porque é isso que o jogo consegue enxergar: ele vê teclas, não letras.
		/// </summary>
		private static bool TryMap(char c, out int scancode, out uint keycode, out bool shift)
		{
			scancode = 0;
			keycode = 0;
			shift = false;

			if (c >= 'a' && c <= 'z')
			{
				scancode = 4 + (c - 'a');
				keycode = c;
				return true;
			}
			if (c >= 'A' && c <= 'Z')
			{
				scancode = 4 + (c - 'A');
				keycode = char.ToLowerInvariant(c);
				shift = true;
				return true;
			}
			if (c >= '1' && c <= '9')
			{
				scancode = 30 + (c - '1');
				keycode = c;
				return true;
			}
			if (c == '0')
			{
				scancode = 39;
				keycode = c;
				return true;
			}

			// Círillico: mesma posição física da letra latina (й = q, ц = w, ...).
			int russian = RussianKeys.IndexOf(c);
			if (russian >= 0)
			{
				char latin = LatinKeys[russian];
				if (latin >= 'a' && latin <= 'z')
				{
					scancode = 4 + (latin - 'a');
					keycode = latin;
					return true;
				}
				return Punctuation(latin, out scancode, out keycode, out shift);
			}
			int russianUpper = RussianKeys.ToUpperInvariant().IndexOf(c);
			if (russianUpper >= 0)
			{
				char latin = LatinKeys[russianUpper];
				if (latin >= 'a' && latin <= 'z')
				{
					scancode = 4 + (latin - 'a');
					keycode = latin;
					shift = true;
					return true;
				}
				return Punctuation(latin, out scancode, out keycode, out shift);
			}

			return Punctuation(c, out scancode, out keycode, out shift);
		}

		private const string RussianKeys = "йцукенгшщзхъфывапролджэячсмитьбюё";
		private const string LatinKeys = "qwertyuiop[]asdfghjkl;\'zxcvbnm,.`";

		private static bool Punctuation(char c, out int scancode, out uint keycode, out bool shift)
		{
			scancode = 0;
			keycode = 0;
			shift = false;
			switch (c)
			{
				case ' ': scancode = 44; keycode = 0x20u; return true;
				case '\n': case '\r': scancode = 40; keycode = 0x0Du; return true;
				case '\t': scancode = 43; keycode = 0x09u; return true;
				case '\b': scancode = 42; keycode = 0x08u; return true;
				case '-': scancode = 45; keycode = 0x2Du; return true;
				case '_': scancode = 45; keycode = 0x2Du; shift = true; return true;
				case '=': scancode = 46; keycode = 0x3Du; return true;
				case '+': scancode = 46; keycode = 0x3Du; shift = true; return true;
				case '[': scancode = 47; keycode = 0x5Bu; return true;
				case '{': scancode = 47; keycode = 0x5Bu; shift = true; return true;
				case ']': scancode = 48; keycode = 0x5Du; return true;
				case '}': scancode = 48; keycode = 0x5Du; shift = true; return true;
				case '\\': scancode = 49; keycode = 0x5Cu; return true;
				case '|': scancode = 49; keycode = 0x5Cu; shift = true; return true;
				case ';': scancode = 51; keycode = 0x3Bu; return true;
				case ':': scancode = 51; keycode = 0x3Bu; shift = true; return true;
				case '\'': scancode = 52; keycode = 0x27u; return true;
				case '"': scancode = 52; keycode = 0x27u; shift = true; return true;
				case '`': scancode = 53; keycode = 0x60u; return true;
				case '~': scancode = 53; keycode = 0x60u; shift = true; return true;
				case ',': scancode = 54; keycode = 0x2Cu; return true;
				case '<': scancode = 54; keycode = 0x2Cu; shift = true; return true;
				case '.': scancode = 55; keycode = 0x2Eu; return true;
				case '>': scancode = 55; keycode = 0x2Eu; shift = true; return true;
				case '/': scancode = 56; keycode = 0x2Fu; return true;
				case '?': scancode = 56; keycode = 0x2Fu; shift = true; return true;
				case '!': scancode = 30; keycode = 0x21u; shift = true; return true;
				case '@': scancode = 31; keycode = 0x40u; shift = true; return true;
				case '#': scancode = 32; keycode = 0x23u; shift = true; return true;
				case '$': scancode = 33; keycode = 0x24u; shift = true; return true;
				case '%': scancode = 34; keycode = 0x25u; shift = true; return true;
				case '^': scancode = 35; keycode = 0x5Eu; shift = true; return true;
				case '&': scancode = 36; keycode = 0x26u; shift = true; return true;
				case '*': scancode = 37; keycode = 0x2Au; shift = true; return true;
				case '(': scancode = 38; keycode = 0x28u; shift = true; return true;
				case ')': scancode = 39; keycode = 0x29u; shift = true; return true;
				default: return false;
			}
		}
	}
}
