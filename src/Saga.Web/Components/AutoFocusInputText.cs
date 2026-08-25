using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Saga.Web.Components;

/// <summary>
/// An <see cref="InputText"/> that takes focus the first time it renders.
/// </summary>
/// <remarks>
/// The plain <c>autofocus</c> attribute is no help on its own: a browser honours it only for
/// elements that are there when the document is parsed, and every in-app link goes through
/// enhanced navigation, which patches the new page into the DOM of the old one. So a page
/// reached by clicking "New proposal" never gets the focus, while the same page reached by a
/// full reload does — which is exactly the confusing half-working behaviour we had.
/// </remarks>
public sealed class AutoFocusInputText : InputText
{
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);

        // Element is captured by InputText's own render; it is null until then.
        if (firstRender && Element is { } element)
        {
            await element.FocusAsync();
        }
    }
}
