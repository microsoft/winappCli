using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;

ReactorApp.Run<Counter>("RApp", width: 480, height: 260);

internal sealed class Counter : Component
{
    static string Label() => "label-v1";

    public override Element Render()
    {
        var (count, setCount) = UseState(0);
        return VStack(12,
            TextBlock($"{Label()} count={count}").FontSize(24),
            Button("Add", () => setCount(count + 1))
        ).Padding(24);
    }
}
