# Security policy

## Supported versions

Only the latest release gets fixes.

## Reporting a vulnerability

Please do **not** open a public issue. Report it privately through
[GitHub's security advisories](https://github.com/crisidev/tentacle/security/advisories/new),
with what you found, how to reproduce it, and which version you tested.

You should hear back within a week. Once there is a fix, it ships in a release and the
advisory is published, with credit unless you prefer otherwise.

## Threat model

Anyone who controls the Jellyfin server, or holds the agent token, decides the ffmpeg
command lines the tentacles run. An ffmpeg command line can read and write any file the
worker's user can, and reach the network. The
[security documentation](docs/security.md) describes what limits that: Landlock confinement
of every job to `TENTACLE_ROOTS`, TLS with a pinned certificate, token rotation, and
the shim socket accepting only the server's own uid.

These are especially in scope:

- a job escaping the `TENTACLE_ROOTS` confinement;
- an agent sending its token to anything but the pinned broker;
- a tentacle (or something posing as one) affecting the server beyond the jobs it is
  given, for example through its verification replies or messages;
- any way to run a job without the token.
