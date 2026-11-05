# Load test

A small tool that sends synthetic Sentry envelopes at the proxy from many simulated systems, to check concurrency behaviour and measure latency. It is not part of the unit tests or the solution, and CI does not run it.

It never sends traffic to Sentry. The proxy forwards to a local stub that answers after a fixed delay, so this is safe to run, but the proxy has to be pointed at the stub (see step 3).

## Run

Use a quiet machine. Other CPU load makes the numbers meaningless.

1. Create synthetic maps (no real obfuscation data) in the proxy's map folder:

   ```
   dotnet run --project tools/LoadTest -- makemap C:\nrmaps
   ```

   This writes `LoadProbe/1.0.0.nrmap` and `LoadProbe/2.0.0.nrmap`. Release `9.9.9` has no map on purpose, so it is forwarded raw.

2. Start the stub (50 ms delay, port 5099):

   ```
   dotnet run -c Release --project tools/LoadTest -- stub 50 5099
   ```

3. Start the proxy with its upstream pointed at the stub. The proxy currently builds the forward URL from `Sentry:RealDsn` only, so you need a temporary local change for the forward URL to hit `http://localhost:5099`. Do not commit that change. Set `Sentry__MappingPath` to the folder from step 1 and set `Authentication__ClientKey` to a throwaway value.

4. Run the load generator:

   ```
   $env:LOADTEST_CLIENT_KEY = "<the proxy's Authentication:ClientKey>"
   dotnet run -c Release --project tools/LoadTest -- load 5000 900 200 0 http://localhost:5000 <projectId>
   ```

   Arguments: total requests, simulated systems, concurrency, gzip (`0` or `1`), proxy URL, project id. The last argument is the numeric id from the proxy's DSN path.

5. Read the result. It prints requests per second, status counts, and p50/p95/p99/max latency. Check that every request returned `200`, and compare the count at `http://localhost:5099/count` with the number sent.

## What to record

Write the numbers into `docs/ISSUES-AND-TODO.md` with the machine and the CPU load at the time: requests per second, p95 latency, status counts, and the proxy's memory. An earlier run on a busy machine gave no valid throughput number.

## Notes

- The client key comes from the `LOADTEST_CLIENT_KEY` environment variable only. Never put it in the code or in a command that gets saved.
- Each request carries one stack frame. A real event has more, so real cost per request is higher.
- Memory use of the proxy is not measured by the tool. Read it from the process (for example Task Manager or `dotnet-counters`).
