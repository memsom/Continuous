﻿using System;
using MonoDevelop.Components.Commands;
using MonoDevelop.Ide;
using MonoDevelop.Refactoring;
using Gtk;
using System.Linq;
using System.Threading.Tasks;
using ICSharpCode.NRefactory.CSharp;
using ICSharpCode.NRefactory.CSharp.Resolver;
using ICSharpCode.NRefactory;
using System.Collections.Generic;

namespace LiveCode.Client.XamarinStudio
{
	public enum Commands
	{
		VisualizeSelection,
		VisualizeClass,
		StopVisualizingClass,
	}

	public class LiveCodeCommandHandler : CommandHandler
	{
		protected HttpClient conn = null;
		protected void Connect ()
		{
			conn = new HttpClient (new Uri ("http://127.0.0.1:" + Http.DefaultPort));
		}

		protected void Alert (string format, params object[] args)
		{
			Log (format, args);
			var parentWindow = IdeApp.Workbench.RootWindow;
			var dialog = new MessageDialog(parentWindow, DialogFlags.DestroyWithParent,
				MessageType.Info, ButtonsType.Ok,
				false,
				format, args);
			dialog.Run ();
			dialog.Destroy ();
		}

		protected async Task<bool> EvalAsync (string code, bool showError)
		{
			var r = await conn.VisualizeAsync (code);
			var err = r.HasErrors;
			if (err) {
				var message = string.Join ("\n", r.Messages.Select (m => m.MessageType + ": " + m.Text));
				if (showError) {
					Alert ("{0}", message);
				}
			}
			return !err;
		}

		protected void Log (string format, params object[] args)
		{
			#if DEBUG
			Log (string.Format (format, args));
			#endif
		}

		protected void Log (string msg)
		{
			#if DEBUG
			Console.WriteLine (msg);
			#endif
		}
	}

	public class VisualizeSelectionHandler : LiveCodeCommandHandler
	{		
		protected override async void Run ()
		{
			base.Run ();

			var doc = IdeApp.Workbench.ActiveDocument;

			if (doc != null) {
				Connect ();
				var code = doc.Editor.SelectedText;

				try {
					await EvalAsync (code, showError: true);
				} catch (Exception ex) {
					Alert ("Could not communicate with the app.\n\n{0}: {1}", ex.GetType (), ex.Message);
				}
			}
		}

		protected override void Update (CommandInfo info)
		{
			base.Update (info);

			var doc = IdeApp.Workbench.ActiveDocument;

			info.Enabled = doc != null && doc.Editor != null && !string.IsNullOrWhiteSpace (doc.Editor.SelectedText);
		}
	}

	public class VisualizeClassHandler : LiveCodeCommandHandler
	{
		public static string monitorTypeName = "";
//		string monitorNamespace = "";

		protected override async void Run ()
		{
			base.Run ();

			var doc = IdeApp.Workbench.ActiveDocument;

			if (doc == null)
				return;

			var typedecl = await FindTypeAtCursor ();

			if (typedecl == null) {				
				Alert ("Could not find a type at the cursor.");
				return;
			}
			
			StartMonitoring ();

			var typeName = typedecl.Name;
			var ns = typedecl.Parent as NamespaceDeclaration;

			var nsName = ns == null ? "" : ns.FullName;

			Console.WriteLine ("MONITOR {0} --- {1}", nsName, typeName);

			if (monitorTypeName != typeName) {
				TypeCode.Clear (); // Reset
				monitorTypeName = typeName;
			}
//			monitorNamespace = nsName;

			await VisualizeTypeAsync (showError: true);
		}

		async Task VisualizeTypeAsync (bool showError)
		{
			//
			// Gobble up all we can about the types in the editor
			//
			var doc = IdeApp.Workbench.ActiveDocument;
			var resolver = await doc.GetSharedResolver ();
			var typeDecls =
				resolver.RootNode.Descendants.
				OfType<TypeDeclaration> ().
				Where (x => !(x.Parent is TypeDeclaration)).
				ToList ();

			var typeTCs = new List<TypeCode> ();
			foreach (var td in typeDecls) {
				typeTCs.Add (TypeCode.Set (td, resolver));
			}

			//
			// Refresh the monitored type
			//
			var monitorTC = TypeCode.Get (monitorTypeName);

			if (string.IsNullOrWhiteSpace (monitorTypeName))
				return;

			var dependsChanged = typeTCs.Any (monitorTC.AllDependencies.Contains);

			if (!dependsChanged)
				return;

			var code = monitorTC.GetLinkedCode ();

			//
			// Send the code to the device
			//
			try {
				Connect ();

				//
				// Declare it
				//
				Log (code.Declarations);
				if (!await EvalAsync (code.Declarations, showError)) return;

				//
				// Show it
				//
				Log (code.ValueExpression);
				if (!await EvalAsync (code.ValueExpression, showError)) return;

			} catch (Exception ex) {
				if (showError) {
					Alert ("Could not communicate with the app.\n\n{0}: {1}", ex.GetType (), ex.Message);
				}
			}
		}

		async Task<TypeDeclaration> FindTypeAtCursor ()
		{
			var doc = IdeApp.Workbench.ActiveDocument;
			var resolver = await doc.GetSharedResolver ();
			var editLoc = doc.Editor.Caret.Location;
			var editTLoc = new TextLocation (editLoc.Line, editLoc.Column);
			var selTypeDecl =
				resolver.RootNode.Descendants.
				OfType<TypeDeclaration> ().
				FirstOrDefault (x => x.StartLocation <= editTLoc && editTLoc <= x.EndLocation);
			return selTypeDecl;
		}

		bool monitoring = false;
		void StartMonitoring ()
		{
			if (monitoring) return;

			IdeApp.Workbench.ActiveDocumentChanged += BindActiveDoc;
			BindActiveDoc (this, EventArgs.Empty);

			monitoring = true;
		}

		MonoDevelop.Ide.Gui.Document boundDoc = null;

		void BindActiveDoc (object sender, EventArgs e)
		{
			var doc = IdeApp.Workbench.ActiveDocument;
			if (boundDoc == doc) {
				return;
			}
			if (boundDoc != null) {				
				boundDoc.DocumentParsed -= ActiveDoc_DocumentParsed;
			}
			boundDoc = doc;
			if (boundDoc != null) {
				boundDoc.DocumentParsed += ActiveDoc_DocumentParsed;
			}
		}

		async void ActiveDoc_DocumentParsed (object sender, EventArgs e)
		{
			var doc = IdeApp.Workbench.ActiveDocument;
			Log ("DOC PARSED {0}", doc.Name);
			await VisualizeTypeAsync (showError: false);
		}

		protected override void Update (CommandInfo info)
		{
			base.Update (info);

			var doc = IdeApp.Workbench.ActiveDocument;

			info.Enabled = doc != null;
		}
	}

	public class StopVisualizingClassHandler : LiveCodeCommandHandler
	{		
		protected override async void Run ()
		{
			base.Run ();
			VisualizeClassHandler.monitorTypeName = "";
			TypeCode.Clear ();
			try {
				Connect ();
				await conn.StopVisualizingAsync ();
			} catch (Exception ex) {
				Log ("ERROR: {0}", ex);
			}
		}

		protected override void Update (CommandInfo info)
		{
			base.Update (info);

			string t = VisualizeClassHandler.monitorTypeName;

			if (string.IsNullOrWhiteSpace (t)) {
				info.Text = "Stop Visualizing Class";
				info.Enabled = false;
			}
			else {
				info.Text = "Stop Visualizing " + t;
				info.Enabled = true;
			}
		}
	}
}

