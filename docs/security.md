# Security

The server decides every command line a tentacle runs, and an ffmpeg command line is
enough to read and write any file the worker's user can, and to reach the network. So
whoever controls the Jellyfin server, or holds the agent token, controls the tentacles'
ffmpeg. This is what limits it.

**Jobs are confined to `TENTACLE_ROOTS`** (Landlock, Linux 5.13+):

* A job can read and write only the directories listed there, read the system (`/usr`,
  `/etc`, `/sys`, `/proc`) and use `/dev` and `/tmp`. Not `/config`, not the token file,
  not another process's files or environment.
* It cannot create symlinks or device nodes. How much more the kernel enforces depends
  on its version:

  | Linux | Landlock ABI | Also blocked |
  |---|---|---|
  | 5.13+ | 1-3 | files only |
  | 6.7+ | 4-5 | listening on TCP |
  | 6.12+ | 6+ | listening on TCP, signalling the agent, abstract unix sockets |

  The dashboard's **Jobs confined** line shows what each tentacle's kernel enforces, and
  the agent logs a warning on kernels older than 6.12.
* The agent starts each job as `tentacle sandbox ... -- ffmpeg ...`, which confines
  itself and then execs ffmpeg.
* Once `TENTACLE_ROOTS` is set, confinement is required: an agent whose kernel lacks
  Landlock does not start. A root the server asks it to verify outside its roots shows
  as not shared, so no job goes there.
* The agent answers those verifications itself, unconfined, so it resolves every
  directory first (symlinks and `..`) and only touches its own probe files directly
  inside a root, and only sample files inside it.
* The dashboard shows **Jobs confined** for each tentacle. Without `TENTACLE_ROOTS` it
  says "no" and the agent logs a warning.
* Outbound network stays open, because Live TV reads from the network. Restrict it with
  a firewall or NetworkPolicy if you can.
* Jobs inherit only the agent's locale, `PATH` and GPU driver variables (`LIBVA_*`,
  `NVIDIA_*`, `OCL_ICD_*`...): never the token, nor any other variable the container
  was given. From the server, a job carries only locale, timezone, `TMPDIR` and driver
  *names* (checked to be bare words, so they cannot point the driver loader at a file).

**TLS by default:**

* On first start the server creates a self-signed certificate
  (`<data>/tentacle/broker.pfx`, 20 years, mode 0600). Agents pin its SHA-256 with
  `TENTACLE_BROKER_FINGERPRINT`. The dashboard shows the fingerprint and an agent
  snippet.
* A wrong fingerprint, or plain `ws://`, never gets as far as sending the token; the
  agent logs why. A `ws://` URL is refused unless `TENTACLE_ALLOW_PLAIN_WS=true`, and
  always when a fingerprint or CA file is set.
* **Your own certificate** (cert-manager, a private CA): `TENTACLE_TLS_CERT` and
  `TENTACLE_TLS_KEY` on the server, or "certificate files" in the settings. Renewals are
  picked up without a restart. Agents trust it with `TENTACLE_CA_FILE`, or through the
  system store for a public CA.
* **Plain `ws://`**: Encryption → None on the server and `TENTACLE_ALLOW_PLAIN_WS=true`
  on the agents. Only inside a network you trust.

**Rotating the token:**

* Dashboard → **Replace token**: the old token keeps working for an overlap you choose
  (24 h by default). Tentacles still on it show "(previous token)". When the overlap
  ends their sessions are dropped and refused.
* Agents re-read `TENTACLE_TOKEN_FILE` on every reconnect: updating a mounted secret is
  enough.
* With `TENTACLE_TOKEN` on the server, rotate it there and set `TENTACLE_PREVIOUS_TOKEN`
  to the old value until every tentacle has the new one.

**And also:** the shim's socket only accepts processes running as the server's own uid
(`SO_PEERCRED`; a connection whose uid cannot be read is refused); the agent port
serves nothing but the agents' WebSockets and a health check; every message decoder is
fuzzed; the soak test SIGKILLs agents and workers under load and then checks for leftover processes, stuck jobs, leaked slots and
file descriptors.

Found a problem? See [SECURITY.md](../SECURITY.md).
