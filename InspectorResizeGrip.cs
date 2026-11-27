using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace AvataRunway
{
    public sealed class InspectorResizeGrip : Grid
    {
        public InspectorResizeGrip()
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        }
    }
}
