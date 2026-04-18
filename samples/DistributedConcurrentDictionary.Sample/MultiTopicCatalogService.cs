using PLC.Shared.DistributedConcurrentDictionary;
using Spectre.Console;

namespace PLC.Shared.DistributedConcurrentDictionary.Sample;

internal sealed class MultiTopicCatalogService
{
    private readonly IDistributedDictionaryFactory _dictionaries;

    public MultiTopicCatalogService(IDistributedDictionaryFactory dictionaries)
    {
        _dictionaries = dictionaries;
    }

    public void Run()
    {
        JsonFusionRedisDistributedDictionary<string, Widget> orders = _dictionaries.GetOrAdd<string, Widget>("orders");
        JsonFusionRedisDistributedDictionary<string, Widget> invoices = _dictionaries.GetOrAdd<string, Widget>("invoices");

        orders.Clear();
        invoices.Clear();

        orders["o1"] = new Widget { Id = 1, Name = "Order-A" };
        invoices["i1"] = new Widget { Id = 100, Name = "Inv-A" };

        AnsiConsole.Write(
            new Panel(
                    $"[bold]orders[/] [o1] → [cyan]{Markup.Escape(orders["o1"].Name)}[/]\n"
                    + $"[bold]invoices[/] [i1] → [cyan]{Markup.Escape(invoices["i1"].Name)}[/]\n"
                    + "[dim]Một service inject IDistributedDictionaryFactory; nhiều topic = nhiều dictionary.[/]")
                .Header("[green]--di[/] AddDistributedDictionaryNode + factory")
                .Border(BoxBorder.Rounded));
    }
}
