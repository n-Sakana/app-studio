namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using System.Windows.Shapes;

    // The drawings this product uses instead of words on its small controls.
    //
    // A control whose whole label is a sentence tells the reader what will
    // happen only after they have read a sentence; a gear, a moon and a pair of
    // outward arrows are read at a glance and mean the same thing in every
    // application on this desktop. So the operations whose meaning is already
    // settled by convention are drawn, and the ones that are particular to this
    // product keep their words.
    //
    // These are paths, not characters. An emoji is a font's opinion of a picture:
    // it changes size, colour and shape between machines, it cannot take the
    // theme's own foreground, and on a Japanese Windows several of them fall back
    // to a monochrome outline that is not the drawing anybody chose. Every icon
    // here is geometry in a 24 by 24 box, stroked in whatever brush the caller is
    // painting with, so it is the same drawing at any size and in either theme.
    //
    // Nothing here is ever the only thing a control carries. Every drawn control
    // is given an accessible name and a tooltip in words, because a picture is a
    // shorthand for people who already know what the thing does, not a substitute
    // for saying it.
    public static class Icons
    {
        public const string Record = "record";
        public const string Snap = "snap";
        public const string Settings = "settings";
        public const string Sun = "sun";
        public const string Moon = "moon";
        public const string Fullscreen = "fullscreen";
        public const string FullscreenExit = "fullscreen-exit";
        public const string Minimise = "minimise";
        public const string Restore = "restore";
        public const string Play = "play";
        public const string Pause = "pause";
        public const string Stop = "stop";
        public const string Folder = "folder";
        public const string ChevronLeft = "chevron-left";
        public const string ChevronRight = "chevron-right";
        public const string ChevronDown = "chevron-down";
        public const string ChevronUp = "chevron-up";
        public const string Check = "check";
        public const string Cross = "cross";
        public const string Copy = "copy";
        public const string Paste = "paste";
        public const string Diff = "diff";
        public const string Build = "build";
        public const string Launch = "launch";
        public const string Report = "report";
        public const string Search = "search";
        public const string Speed = "speed";
        public const string Workflow = "workflow";
        public const string Assistant = "assistant";
        public const string Module = "module";
        public const string Refresh = "refresh";
        public const string Wrap = "wrap";
        public const string Pointer = "pointer";

        // The drawings, as path data in a 24 by 24 box. Stroked rather than
        // filled unless the shape is solid by nature - a record dot and a play
        // triangle are solid things, an outline of either reads as an empty one.
        private static readonly Dictionary<string, string[]> Paths = Shapes();

        private static Dictionary<string, string[]> Shapes()
        {
            Dictionary<string, string[]> map = new Dictionary<string, string[]>(StringComparer.Ordinal);
            // Stroked shapes.
            map.Add(Snap, new string[] { "M 3,8 L 3,19 L 21,19 L 21,8 L 17,8 L 15.5,5 L 8.5,5 L 7,8 Z", "M 16,13 A 4,4 0 1 1 8,13 A 4,4 0 1 1 16,13 Z" });
            map.Add(Settings, new string[] { "M 15,12 A 3,3 0 1 1 9,12 A 3,3 0 1 1 15,12 Z", "M 12,2 L 13.4,2 L 13.9,4.6 L 16.2,5.6 L 18.3,4 L 20,5.7 L 18.4,7.8 L 19.4,10.1 L 22,10.6 L 22,13.4 L 19.4,13.9 L 18.4,16.2 L 20,18.3 L 18.3,20 L 16.2,18.4 L 13.9,19.4 L 13.4,22 L 10.6,22 L 10.1,19.4 L 7.8,18.4 L 5.7,20 L 4,18.3 L 5.6,16.2 L 4.6,13.9 L 2,13.4 L 2,10.6 L 4.6,10.1 L 5.6,7.8 L 4,5.7 L 5.7,4 L 7.8,5.6 L 10.1,4.6 L 10.6,2 Z" });
            map.Add(Sun, new string[] { "M 16,12 A 4,4 0 1 1 8,12 A 4,4 0 1 1 16,12 Z", "M 12,2 L 12,4", "M 12,20 L 12,22", "M 2,12 L 4,12", "M 20,12 L 22,12", "M 4.9,4.9 L 6.3,6.3", "M 17.7,17.7 L 19.1,19.1", "M 4.9,19.1 L 6.3,17.7", "M 17.7,6.3 L 19.1,4.9" });
            map.Add(Moon, new string[] { "M 20.5,14.5 A 9,9 0 1 1 9.5,3.5 A 7,7 0 0 0 20.5,14.5 Z" });
            map.Add(Fullscreen, new string[] { "M 4,9 L 4,4 L 9,4", "M 15,4 L 20,4 L 20,9", "M 20,15 L 20,20 L 15,20", "M 9,20 L 4,20 L 4,15" });
            map.Add(FullscreenExit, new string[] { "M 9,4 L 9,9 L 4,9", "M 20,9 L 15,9 L 15,4", "M 15,20 L 15,15 L 20,15", "M 4,15 L 9,15 L 9,20" });
            map.Add(Minimise, new string[] { "M 4,6 L 20,6", "M 4,12 L 20,12", "M 4,18 L 20,18", "M 8,9 L 12,12.6 L 16,9" });
            map.Add(Restore, new string[] { "M 3,5 L 21,5 L 21,19 L 3,19 Z", "M 9,5 L 9,19", "M 15,5 L 15,19" });
            map.Add(Stop, new string[] { "M 6,6 L 18,6 L 18,18 L 6,18 Z" });
            map.Add(Folder, new string[] { "M 3,7 L 3,19 L 21,19 L 21,9 L 12,9 L 10,6 L 4,6 A 1,1 0 0 0 3,7 Z" });
            map.Add(ChevronLeft, new string[] { "M 15,5 L 8,12 L 15,19" });
            map.Add(ChevronRight, new string[] { "M 9,5 L 16,12 L 9,19" });
            map.Add(ChevronDown, new string[] { "M 5,9 L 12,16 L 19,9" });
            map.Add(ChevronUp, new string[] { "M 5,15 L 12,8 L 19,15" });
            map.Add(Check, new string[] { "M 4,12.5 L 9.5,18 L 20,6.5" });
            map.Add(Cross, new string[] { "M 6,6 L 18,18", "M 18,6 L 6,18" });
            map.Add(Copy, new string[] { "M 9,9 L 20,9 L 20,20 L 9,20 Z", "M 15,5 L 4,5 L 4,16" });
            map.Add(Paste, new string[] { "M 8,4 L 6,4 A 1,1 0 0 0 5,5 L 5,20 L 19,20 L 19,5 A 1,1 0 0 0 18,4 L 16,4", "M 9,2.5 L 15,2.5 L 15,6 L 9,6 Z" });
            map.Add(Diff, new string[] { "M 7,3 L 7,15", "M 4,6 L 10,6", "M 4,20 L 10,20", "M 14,10 L 20,10", "M 14,15 L 20,15", "M 17,4 L 17,7" });
            map.Add(Build, new string[] { "M 12,2 L 21,7 L 21,17 L 12,22 L 3,17 L 3,7 Z", "M 3,7 L 12,12 L 21,7", "M 12,12 L 12,22" });
            map.Add(Launch, new string[] { "M 14,4 L 20,4 L 20,10", "M 20,4 L 11,13", "M 18,14 L 18,19 A 1,1 0 0 1 17,20 L 5,20 A 1,1 0 0 1 4,19 L 4,7 A 1,1 0 0 1 5,6 L 10,6" });
            map.Add(Report, new string[] { "M 5,3 L 15,3 L 19,7 L 19,21 L 5,21 Z", "M 15,3 L 15,7 L 19,7", "M 8,12 L 16,12", "M 8,16 L 13,16" });
            map.Add(Search, new string[] { "M 18,11 A 7,7 0 1 1 4,11 A 7,7 0 1 1 18,11 Z", "M 16,16 L 21,21" });
            map.Add(Speed, new string[] { "M 3,18 A 9,9 0 1 1 21,18", "M 12,18 L 16.5,10" });
            map.Add(Workflow, new string[] { "M 4,5 L 20,5", "M 4,12 L 20,12", "M 4,19 L 20,19", "M 7.5,5 L 7.5,19" });
            map.Add(Assistant, new string[] { "M 21,15 A 2,2 0 0 1 19,17 L 8,17 L 4,21 L 4,6 A 2,2 0 0 1 6,4 L 19,4 A 2,2 0 0 1 21,6 Z", "M 9,10.5 L 9,11", "M 15,10.5 L 15,11" });
            map.Add(Module, new string[] { "M 5,3 L 15,3 L 19,7 L 19,21 L 5,21 Z", "M 15,3 L 15,7 L 19,7", "M 10,12 L 8,14.5 L 10,17", "M 14,12 L 16,14.5 L 14,17" });
            map.Add(Refresh, new string[] { "M 20,12 A 8,8 0 1 1 17,5.8", "M 20,3 L 20,9 L 14,9" });
            map.Add(Wrap, new string[] { "M 4,6 L 20,6", "M 4,12 L 16,12 A 3,3 0 0 1 16,18 L 12,18", "M 14,15.5 L 11.5,18 L 14,20.5", "M 4,18 L 8,18" });
            map.Add(Pointer, new string[] { "M 6,3 L 6,18 L 10,14.5 L 12.5,20 L 15,19 L 12.5,13.5 L 17.5,13.5 Z" });
            // Solid shapes.
            map.Add(Record, new string[] { "M 19,12 A 7,7 0 1 1 5,12 A 7,7 0 1 1 19,12 Z" });
            map.Add(Play, new string[] { "M 7,4.5 L 19,12 L 7,19.5 Z" });
            map.Add(Pause, new string[] { "M 7,5 L 10.5,5 L 10.5,19 L 7,19 Z", "M 13.5,5 L 17,5 L 17,19 L 13.5,19 Z" });
            return map;
        }

        private static bool IsSolid(string name)
        {
            return String.Equals(name, Record, StringComparison.Ordinal) ||
                String.Equals(name, Play, StringComparison.Ordinal) ||
                String.Equals(name, Pause, StringComparison.Ordinal);
        }

        public static bool Has(string name)
        {
            return name != null && Paths.ContainsKey(name);
        }

        // One drawing, at the size asked for, painted in the brush asked for.
        //
        // The brush is bound rather than copied. Every colour in this product is
        // a live brush the theme switch writes into, so an icon made in the light
        // theme repaints itself when the operator asks for the dark one instead
        // of staying the colour it was drawn in.
        public static UIElement Make(string name, double size, Brush stroke)
        {
            Canvas canvas = new Canvas();
            canvas.Width = 24;
            canvas.Height = 24;
            string[] parts;
            if (name == null || !Paths.TryGetValue(name, out parts)) parts = new string[0];
            bool solid = IsSolid(name);
            for (int index = 0; index < parts.Length; index++)
            {
                Path shape = new Path();
                shape.Data = Geometry.Parse(parts[index]);
                if (solid)
                {
                    shape.Fill = stroke;
                }
                else
                {
                    shape.Stroke = stroke;
                    // Two units in a 24 unit box. Thinner than this and the icon
                    // disappears next to the weight of the type beside it; thicker
                    // and the closed shapes fill in at 16 pixels.
                    shape.StrokeThickness = 2;
                    shape.StrokeStartLineCap = PenLineCap.Round;
                    shape.StrokeEndLineCap = PenLineCap.Round;
                    shape.StrokeLineJoin = PenLineJoin.Round;
                }
                canvas.Children.Add(shape);
            }
            Viewbox box = new Viewbox();
            box.Width = size;
            box.Height = size;
            box.Stretch = Stretch.Uniform;
            box.Child = canvas;
            // The drawing is not a second thing for a screen reader to announce:
            // the control around it already carries the name in words.
            box.Focusable = false;
            return box;
        }

        public static UIElement Make(string name, double size)
        {
            return Make(name, size, Theme.TextSub);
        }
    }
}
