using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace LazyReply
{
    // Reads the text box that has the keyboard focus in any app (browser chat boxes included),
    // through Windows UI Automation. Only real text inputs are read: never password boxes,
    // never a whole web page.
    static class FocusReader
    {
        public class Info
        {
            public bool Usable;
            public string Text = "";
            public Rectangle Bounds = Rectangle.Empty;
            public int ProcessId;
        }

        static volatile bool reading;

        // UI Automation can hang on a busy app, so it runs on a worker thread with a time limit.
        public static async Task<Info> ReadAsync(int timeoutMs)
        {
            if (reading) return new Info();
            reading = true;
            var work = Task.Run(delegate
            {
                try { return Read(); }
                catch (Exception ex) { Log.Write("focus read failed: " + ex.GetType().Name); return new Info(); }
                finally { reading = false; }
            });
            if (await Task.WhenAny(work, Task.Delay(timeoutMs)) == work) return work.Result;
            Log.Write("focus read timed out");
            return new Info();
        }

        static Info Read()
        {
            var info = new Info();
            AutomationElement el = AutomationElement.FocusedElement;
            if (el == null) return info;
            AutomationElement.AutomationElementInformation c = el.Current;
            info.ProcessId = c.ProcessId;
            if (c.IsPassword) return info;

            ValuePattern value = null;
            object pat;
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out pat)) value = (ValuePattern)pat;

            bool edit = c.ControlType == ControlType.Edit;
            // Rich editors report "Document"; a web page body does too, but it is read-only.
            bool editableDocument = c.ControlType == ControlType.Document && value != null && !value.Current.IsReadOnly;
            if (!edit && !editableDocument) return info;
            if (value != null && value.Current.IsReadOnly) return info;

            string text = value != null ? value.Current.Value : null;
            if (string.IsNullOrEmpty(text) && el.TryGetCurrentPattern(TextPattern.Pattern, out pat))
                text = ((TextPattern)pat).DocumentRange.GetText(5000);
            info.Text = (text ?? "").Replace("​", "").Replace("﻿", "");

            System.Windows.Rect r = c.BoundingRectangle;
            if (!r.IsEmpty && r.Width > 0 && r.Height > 0)
                info.Bounds = new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
            info.Usable = true;
            return info;
        }
    }
}
