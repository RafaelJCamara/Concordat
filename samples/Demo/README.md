# Concordat demo

Four small services (`checkout`, `billing`, `gateway`, `ledger`) send orders and payments to each other over
RabbitMQ under two contracts, one that **enforces** and one that only **monitors**. A few messages are wrong on purpose,
so the registry's web app fills up with contracts, reported violations and a breaking change waiting for approval.

```bash
CONCORDAT_API_KEY=<an API key with subject:write and contract:write> dotnet run --project samples/Demo
```

It needs the registry and RabbitMQ running and an API key. **[docs/DEMO.md](../../docs/DEMO.md) is the walkthrough**: how
to start them, what you will see in the terminal and in the web app, things to change and what they do, every option and
setting, and what to do when it will not start.

Not the quickstart: [`samples/Quickstart`](../Quickstart) publishes two messages and exits, and is the smallest possible
use of the SDK. This one keeps running, so there is something to look at.

| File | |
|---|---|
| `Seed.cs` | What a pipeline does: subjects, versions, contracts and bindings, and one breaking version held at the gate |
| `Service.cs` | What a service does: a named client, an enforcing channel, and the timer that reports violations |
| `Traffic.cs` | The messages and how they are wrong |
| `Program.cs` | The topology, the four services and the loop |

It sits outside `Concordat.slnx` on purpose, like the other samples: the solution does not depend on a sample. CI builds it.
