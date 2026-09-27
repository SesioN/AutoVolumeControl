using System.Linq;
using System.Windows.Forms;

namespace AutoVolumeControl
{
    /// <summary>
    /// A table in the menu that is never shorter than its content: after each layout it grows to its lowest child,
    /// so a row can never hang out of it and cover the separator below.
    /// </summary>
    sealed class MenuTable : TableLayoutPanel
    {
        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);

            int bottom = Controls.Cast<Control>()
                .Select(c => c.Bottom + c.Margin.Bottom)
                .DefaultIfEmpty(0)
                .Max() + Padding.Bottom;
            if (bottom > Height)
                Height = bottom;
        }
    }
}
