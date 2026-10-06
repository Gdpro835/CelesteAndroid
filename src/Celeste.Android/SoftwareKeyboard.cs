using System;
using Android.App;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;

namespace CelesteAndroid
{
	/// <summary>
	/// Mostra/esconde o teclado do sistema.
	/// <para>
	/// O caminho "de fábrica" é o SDL: o módulo de patches chama <c>TextInputEXT.StartTextInput</c>,
	/// o SDL cria o campo de texto dele e entrega o que for digitado como SDL_EVENT_TEXT_INPUT.
	/// Só que o pedido que o SDL faz é implícito, e o sistema o ignora quando acha que existe um
	/// teclado físico — um controle Bluetooth conta como um. Aqui o mesmo pedido é repetido com
	/// <see cref="ShowFlags.Forced"/> na view em foco (é o pedido que passa por cima disso) e
	/// repetido mais uma vez, porque o campo do SDL pode ainda não estar em foco na primeira.
	/// </para>
	/// <para>
	/// Quando nem assim o teclado abre, o motivo vai para o logcat e para um Toast: sem adb, é a
	/// única forma de o jogador saber o que falhou.
	/// </para>
	/// </summary>
	internal sealed class SoftwareKeyboard
	{
		private readonly Activity activity;
		private readonly Handler main = new(Looper.MainLooper!);
		private InputMethodManager? manager;

		// Cada pedido invalida as verificações do anterior: o jogador pode fechar o teclado antes
		// de o sistema responder, e um "abre o teclado" atrasado não pode desfazer isso.
		private int generation;

		public SoftwareKeyboard(Activity activity) => this.activity = activity;

		private InputMethodManager? Manager
		{
			get
			{
				try
				{
					return manager ??= activity.GetSystemService(Android.Content.Context.InputMethodService) as InputMethodManager;
				}
				catch (Exception)
				{
					return null;
				}
			}
		}

		/// <summary>Pedido do jogo (thread do SDL): mostrar ou esconder o teclado.</summary>
		public void Set(bool show)
		{
			int id = ++generation;
			main.Post(() =>
			{
				if (show)
				{
					// O SDL acabou de criar e pedir foco para o campo de texto dele, e a troca de foco
					// só vale depois da próxima passada de layout — daí a pequena espera.
					main.PostDelayed(() => Run(id, () => Show(id, attempt: 1)), 120);
				}
				else
				{
					Run(id, Hide);
				}
			});
		}

		/// <summary>O teclado está à vista? (lido da thread do jogo)</summary>
		public bool IsShown()
		{
			try
			{
				return Manager?.IsAcceptingText ?? false;
			}
			catch (Exception)
			{
				return false;
			}
		}

		private void Run(int id, Action action)
		{
			if (id != generation)
			{
				return;
			}
			try
			{
				action();
			}
			catch (Exception e)
			{
				Log.Warn(GameActivity.LogTag, "teclado: " + e);
			}
		}

		private void Show(int id, int attempt)
		{
			InputMethodManager? maybe = Manager;
			if (maybe == null)
			{
				Diagnose("sem InputMethodManager");
				return;
			}
			InputMethodManager ime = maybe;
			if (ime.IsAcceptingText)
			{
				return;
			}

			View? focus = activity.CurrentFocus;
			if (focus == null)
			{
				Diagnose("sem campo em foco");
				return;
			}

			Log.Info(GameActivity.LogTag, $"teclado: pedido forçado (tentativa {attempt}, foco={focus.GetType().Name})");
			ime.ShowSoftInput(focus, ShowFlags.Forced);

			main.PostDelayed(() => Run(id, () =>
			{
				if (ime.IsAcceptingText)
				{
					Log.Info(GameActivity.LogTag, "teclado: aberto");
					return;
				}
				if (attempt == 1)
				{
					Show(id, attempt: 2);
					return;
				}
				Diagnose("o sistema recusou o pedido");
			}), 300);
		}

		private void Hide()
		{
			InputMethodManager? ime = Manager;
			View? focus = activity.CurrentFocus;
			IBinder? token = focus?.WindowToken ?? activity.Window?.DecorView?.WindowToken;
			if (ime != null && token != null)
			{
				ime.HideSoftInputFromWindow(token, HideSoftInputFlags.None);
			}
			// Solta o foco do campo do SDL: senão o teclado volta na próxima troca de foco da janela.
			focus?.ClearFocus();
		}

		private void Diagnose(string reason)
		{
			// O nome da classe em foco já diz o que interessa: SDLDummyEdit (o campo do SDL, que
			// entrega o que for digitado) ou SDLSurface (o toque, onde o texto não iria a lugar nenhum).
			View? focus = activity.CurrentFocus;
			string state = "foco=" + (focus?.GetType().Name ?? "nada")
				+ ", janela=" + (activity.HasWindowFocus ? "com foco" : "sem foco");
			Log.Warn(GameActivity.LogTag, $"teclado: {reason} ({state})");
			try
			{
				Toast.MakeText(activity, $"Teclado: {reason} ({state})", ToastLength.Long)?.Show();
			}
			catch (Exception)
			{
				// Um Toast não pode derrubar o jogo.
			}
		}
	}
}
