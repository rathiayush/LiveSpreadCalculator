// SpreadTrader.Engine is a class library, not a standalone process. Its hosted services
// (Services/*) are registered directly into the LiveSpreadCalculator Api host via
// AddHostedService<T>() in Program.cs there, so the whole pipeline - ticker ingestion, spread
// calculation, trigger evaluation, order dispatch - runs as a single process/singleton
// alongside the SignalR hub that pushes updates to the React dashboard.
//
// Why one process instead of a separate worker: the tick store and trigger engine must be a
// singleton anyway (Kite allows only 3 WebSocket connections per API key, and running the
// engine twice would double-fire orders), so there's no scaling benefit to splitting it out
// today. If you outgrow this later, the engine only depends on the abstractions in
// SpreadTrader.Core (ITickStore, ISpreadPublisher, ITriggerEventSink, IOrderGateway) - swap
// their SignalR-backed implementations for a message-queue-backed one and the engine can move
// to its own container without any changes to this project.
