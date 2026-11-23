# Observability

## Metrics

Tentacle's metrics are in Jellyfin's own `/metrics` (enable `EnableMetrics` in
`system.xml` and restart Jellyfin). There is no separate endpoint to scrape.

| Metric | Labels | |
|---|---|---|
| `tentacle_jobs_total` | node, kind, outcome | Finished jobs. `node="local"` is the server. |
| `tentacle_job_duration_seconds` | node, kind | Histogram. |
| `tentacle_jobs_running` | node, kind | |
| `tentacle_running_job_start_time_seconds` | job_id, node, kind, user, client, item | One series per running job (user, client and item only with "Name viewers in metrics"). |
| `tentacle_placements_total` | kind, node, reason | Every placement decision and why. |
| `tentacle_cross_vendor_total` | node, outcome, kind | Transcodes and trickplay jobs (`kind`) considered for a tentacle with another vendor's GPU: `rebuilt`, `shadow`, or `skipped:<why>` (`no-match`, `ambiguous`, `no-anchor`, `changed`, `hdr`, `copy`, `codec`, `no-video`, `job-type`, `vendor`, `error`; trickplay also `shape` and `unavailable`), or `refused:mismatch` (the shim's command line was not the recorded rebuild). A trickplay `skipped:no-match` means Jellyfin no longer starts trickplay the way Tentacle predicts it: watch it after Jellyfin upgrades. |
| `tentacle_node_state` | node, state | A tentacle seen since the server started shows as `Disconnected` once gone. |
| `tentacle_node_slots`, `tentacle_node_slots_used` | node, class | |
| `tentacle_node_check` | node, check, required | Each verification check, 1 = passed. A check that does not apply is left out: the server's hardware test on a tentacle with no GPU or another vendor's (the plugin's page shows it as not tested). |
| `tentacle_node_info` | node, versions, host, arch, GPU | |
| `tentacle_node_capability` | node, capability | Measured encodes and decodes. |
| `tentacle_server_info` | name, role | The server's name (`node="local"` elsewhere) and role. |
| `tentacle_broker_up`, `tentacle_placement_mode` | mode | |
| `tentacle_cross_vendor_mode` | mode | The cross-vendor mode in effect: its own setting, never further than `tentacle_placement_mode`. |

## Logs

One logfmt line per finished job in Jellyfin's log:

```
tentacle_job job=… kind=… node=… reason=… would_run_on=… outcome=… exit=… duration_ms=… nice=…
```

Agents log lines starting with `[tentacle]`. A remote job that is lost prints
`[tentacle] job <id> on <node> lost: <reason>` into Jellyfin's FFmpeg log.

## Alerts and Grafana

[`deploy/prometheus/tentacle.rules.yaml`](../deploy/prometheus/tentacle.rules.yaml) has
alert rules, with promtool tests next to them:

| Alert | When |
|---|---|
| TentaclesUnused | Tentacles are usable, yet most transcodes ran on the server. |
| TentacleNodeDown | A tentacle that connected earlier is gone. |
| TentacleIncompatible | A tentacle runs a different ffmpeg. |
| TentacleStuck | Verifying or CoolingDown for 20 minutes. |
| TentacleCheckFailing | A required check fails. |
| TentacleTranscodeFailures | Several transcodes failed on one node in an hour. |
| TentaclePathIneligible | Jobs ran on the server because a path is not shared. |
| TentacleCapacity | Jobs ran on the server because every tentacle was full. |
| TentacleCrossVendorNotRebuilding | Jobs were picked for another vendor's GPU for 6h, and none was rebuilt (after a Jellyfin upgrade, look here). |
| TentacleCrossVendorRefused | A shim brought another command line than the rebuild recorded for it. |
| TentacleBrokerDown | The broker is not running, or Jellyfin exports no metrics. |

[`deploy/grafana/tentacle.json`](../deploy/grafana/tentacle.json) is a Grafana board with
Prometheus and Loki datasource variables: nodes, their state and GPU, transcodes by node,
slots, placement reasons, failures, durations by node, cross-vendor decisions, and the
job log lines. Both files are also attached
to every release.
