using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CelesteAndroid.Touch
{
	/// <summary>
	/// Minimal SDL3 interop for the on-screen controls.
	///
	/// The port doesn't go through FNA's <c>TouchPanel</c>: <c>FNAPlatform.UpdateTouchPanelState</c>
	/// polls touch device index 0 only and Android reports several devices (see
	/// docs/ARCHITECTURE.md), so touches never reach it. Reading the fingers straight from SDL and
	/// turning them into key events achieves the same without patching FNA.
	///
	/// The structures and constants below follow the SDL 3.4.16 sources the port pins
	/// (external/SDL): include/SDL3/SDL_events.h, SDL_touch.h, SDL_scancode.h, SDL_keycode.h.
	/// </summary>
	internal static class SdlInput
	{
		private const string Library = "SDL3";

		// SDL_EventType: SDL_EVENT_KEY_DOWN, SDL_EVENT_KEY_UP.
		private const uint EventKeyDown = 0x300;
		private const uint EventKeyUp = 0x301;

		/// <summary>SDL_Finger: id + position normalized to the window + pressure.</summary>
		[StructLayout(LayoutKind.Sequential)]
		internal struct Finger
		{
			public ulong Id;
			public float X;
			public float Y;
			public float Pressure;
		}

		/// <summary>
		/// SDL_Event is a 128-byte union (SDL_events.h: <c>Uint8 padding[128]</c>) and SDL_PushEvent
		/// copies the whole thing, so <see cref="Size"/> is not optional. Only the fields of
		/// SDL_KeyboardEvent are used; the offsets match its layout.
		/// </summary>
		[StructLayout(LayoutKind.Explicit, Size = 128)]
		private struct SDL_Event
		{
			[FieldOffset(0)] public uint Type;
			[FieldOffset(8)] public ulong Timestamp;
			[FieldOffset(16)] public uint WindowId;
			[FieldOffset(20)] public uint Which;
			[FieldOffset(24)] public int Scancode;
			[FieldOffset(28)] public uint Keycode;
			[FieldOffset(36)] public byte Down;
			[FieldOffset(37)] public byte Repeat;
		}

		[DllImport(Library, EntryPoint = "SDL_GetTouchDevices", CallingConvention = CallingConvention.Cdecl)]
		private static extern IntPtr SDL_GetTouchDevices(out int count);

		[DllImport(Library, EntryPoint = "SDL_GetTouchFingers", CallingConvention = CallingConvention.Cdecl)]
		private static extern IntPtr SDL_GetTouchFingers(ulong touchId, out int count);

		[DllImport(Library, EntryPoint = "SDL_free", CallingConvention = CallingConvention.Cdecl)]
		private static extern void SDL_free(IntPtr memory);

		[DllImport(Library, EntryPoint = "SDL_PumpEvents", CallingConvention = CallingConvention.Cdecl)]
		private static extern void SDL_PumpEvents();

		[DllImport(Library, EntryPoint = "SDL_PushEvent", CallingConvention = CallingConvention.Cdecl)]
		[return: MarshalAs(UnmanagedType.I1)]
		private static extern bool SDL_PushEvent(ref SDL_Event evt);

		[DllImport(Library, EntryPoint = "SDL_SetHint", CallingConvention = CallingConvention.Cdecl)]
		private static extern bool SDL_SetHint(
			[MarshalAs(UnmanagedType.LPUTF8Str)] string name,
			[MarshalAs(UnmanagedType.LPUTF8Str)] string value);

		[DllImport(Library, EntryPoint = "SDL_GetError", CallingConvention = CallingConvention.Cdecl)]
		private static extern IntPtr SDL_GetError();

		/// <summary>
		/// Faz o SDL mostrar o teclado da tela mesmo quando ele acha que existe um teclado físico
		/// (o padrão do hint é "auto": mostra só se não houver). O valor precisa estar definido
		/// antes de SDL_StartTextInput, e é isso que o botão de teclado do pad significa.
		/// </summary>
		public static void EnableScreenKeyboard() => SDL_SetHint("SDL_ENABLE_SCREEN_KEYBOARD", "1");

		/// <summary>Último erro do SDL (vazio quando não houve), para o log do pad.</summary>
		public static string LastError()
		{
			IntPtr error = SDL_GetError();
			return error == IntPtr.Zero ? string.Empty : (Marshal.PtrToStringUTF8(error) ?? string.Empty);
		}

		/// <summary>
		/// Fills <paramref name="fingers"/> with the active fingers of every touch device SDL
		/// reports (merging them: which device index is the screen changes between devices and
		/// sessions). Returns the number of fingers.
		/// </summary>
		public static int PollFingers(List<Finger> fingers)
		{
			fingers.Clear();
			// Android queues the touches on another thread and only turns them into SDL finger
			// state when the events are pumped. FNA pumps at the start of the tick, so pumping
			// here too takes one frame off the input lag (same thread as FNA: the SDL one).
			SDL_PumpEvents();

			IntPtr devices = SDL_GetTouchDevices(out int deviceCount);
			if (devices == IntPtr.Zero || deviceCount <= 0)
			{
				return 0;
			}
			try
			{
				for (int i = 0; i < deviceCount; i++)
				{
					ulong device = unchecked((ulong)Marshal.ReadInt64(devices, i * sizeof(long)));
					IntPtr array = SDL_GetTouchFingers(device, out int count);
					if (array == IntPtr.Zero || count <= 0)
					{
						continue;
					}
					try
					{
						for (int f = 0; f < count; f++)
						{
							IntPtr finger = Marshal.ReadIntPtr(array, f * IntPtr.Size);
							if (finger != IntPtr.Zero)
							{
								fingers.Add(Marshal.PtrToStructure<Finger>(finger));
							}
						}
					}
					finally
					{
						SDL_free(array);
					}
				}
			}
			finally
			{
				SDL_free(devices);
			}
			return fingers.Count;
		}

		/// <summary>
		/// Pushes a key press/release into SDL's event queue. FNA polls the queue at the start of
		/// every tick and updates <c>Keyboard.keys</c> from it (SDL3_FNAPlatform.PollEvents), so the
		/// game sees the key exactly like a real one.
		/// </summary>
		public static bool PushKey(GameKey key, bool pressed)
		{
			(int scancode, uint keycode) = key.Codes();
			return PushScancode(scancode, keycode, pressed);
		}

		/// <summary>
		/// Mesma coisa para qualquer tecla, não só as do <see cref="GameKey"/>: é o que a digitação
		/// precisa (ver VirtualTyping), porque o teclado do sistema entrega caracteres, não teclas.
		/// </summary>
		public static bool PushScancode(int scancode, uint keycode, bool pressed)
		{
			SDL_Event evt = default;
			evt.Type = pressed ? EventKeyDown : EventKeyUp;
			evt.Scancode = scancode;
			evt.Keycode = keycode;
			evt.Down = pressed ? (byte)1 : (byte)0;
			return SDL_PushEvent(ref evt);
		}
	}
}
