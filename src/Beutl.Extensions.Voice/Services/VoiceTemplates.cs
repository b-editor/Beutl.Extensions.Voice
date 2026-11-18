using Beutl.Editor.Services;
using Beutl.Graphics;
using Beutl.ProjectSystem;
using Microsoft.Extensions.DependencyInjection;

namespace Beutl.Extensions.Voice.Services;

// Speech takes its characters and captions from the template library, never from the timeline.
internal static class VoiceTemplates
{
    // The saved elements and drawables in the library of the editor the speech is edited in; each picker offers the ones
    // it can use.
    public static Func<IEnumerable<ObjectTemplateItem>> Loader(IServiceProvider services)
    {
        var library = services.GetService<ObjectTemplateService>();
        return () => library == null ? []
            : library.FindByBaseType(typeof(Element)).Concat(library.FindByBaseType(typeof(Drawable)));
    }

    // A saved drawable is wrapped in an element, so both kinds are searched the same way.
    public static Element? CreateElement(ObjectTemplateItem template)
    {
        // CreateInstance also resolves packaged template assets relative to the template file.
        var instance = template.CreateInstance();
        Element element;
        if (instance is Element savedElement) element = savedElement;
        else if (instance is Drawable drawable)
        {
            element = new Element { Length = { Value = MediaTime.FromSeconds(1) } };
            element.AddObject(drawable);
        }
        else return null;
        element.Name.Value = template.Name.Value;
        return element;
    }
}
