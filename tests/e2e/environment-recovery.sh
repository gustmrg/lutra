#!/usr/bin/env bash
set -euo pipefail

readonly sentinel='LUTRA_E2E_SECRET_SENTINEL_7af43c'
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

fail() {
  echo "environment recovery drill failed: $*" >&2
  exit 1
}

validate_work_directory() {
  case "$(basename "$1")" in
    tmp.*|lutra-environment-e2e*) ;;
    *) fail 'LUTRA_E2E_WORK must name a dedicated tmp.* or lutra-environment-e2e* directory' ;;
  esac
}

if [[ "$(uname -s)" != Linux ]]; then
  command -v docker >/dev/null || fail 'Docker is required to run the Linux drill from this host'
  work="${LUTRA_E2E_WORK:-$(mktemp -d)}"
  validate_work_directory "$work"
  publish="$work/publish"
  case "$(uname -m)" in
    arm64|aarch64) rid=linux-arm64 ;;
    x86_64|amd64) rid=linux-x64 ;;
    *) fail "unsupported host architecture: $(uname -m)" ;;
  esac
  dotnet publish "$repo_root/src/Lutra.CLI/Lutra.CLI.csproj" -c Release -r "$rid" \
    --self-contained -p:PublishSingleFile=true -o "$publish"
  docker_args=(
    --rm
    --mount "type=bind,source=$repo_root,target=/repo,readonly" \
    --mount "type=bind,source=$publish,target=/lutra,readonly" \
    --mount "type=bind,source=$work,target=$work" \
    --workdir /repo
    -e LUTRA_E2E_LUTRA=/lutra/Lutra.CLI
    -e "LUTRA_E2E_WORK=$work"
    -e "LUTRA_E2E_VOLUME=${LUTRA_E2E_VOLUME:-0}"
  )
  if [[ "${LUTRA_E2E_VOLUME:-0}" == 1 ]]; then
    docker_socket="$(docker context inspect --format '{{.Endpoints.docker.Host}}')"
    docker_socket="${docker_socket#unix://}"
    docker_args+=(--mount "type=bind,source=$docker_socket,target=/var/run/docker.sock")
  fi
  docker run "${docker_args[@]}" ubuntu:24.04 bash tests/e2e/environment-recovery.sh
  exit
fi

install_prerequisites() {
  local missing=()
  ldconfig -p 2>/dev/null | grep -q 'libicuuc' || missing+=(libicu-dev)
  command -v gzip >/dev/null || missing+=(gzip)
  command -v tar >/dev/null || missing+=(tar)
  command -v nginx >/dev/null || missing+=(nginx)
  command -v systemd-analyze >/dev/null || missing+=(systemd)
  command -v runuser >/dev/null || missing+=(util-linux)
  if [[ "${LUTRA_E2E_VOLUME:-0}" == 1 ]]; then
    command -v docker >/dev/null || missing+=(docker.io)
  fi
  if (( ${#missing[@]} == 0 )); then
    return
  fi
  local elevate=()
  if (( EUID != 0 )); then
    command -v sudo >/dev/null || fail "missing prerequisites: ${missing[*]}"
    elevate=(sudo)
  fi
  "${elevate[@]}" apt-get update -qq
  DEBIAN_FRONTEND=noninteractive "${elevate[@]}" apt-get install -y -qq --no-install-recommends \
    "${missing[@]}"
}

install_prerequisites

work="${LUTRA_E2E_WORK:-$(mktemp -d)}"
validate_work_directory "$work"
rm -rf "$work/source" "$work/backups" "$work/state" "$work/restore" "$work/inspect" "$work/home"
mkdir -p "$work/source" "$work/backups" "$work/state" "$work/restore/tmp" "$work/inspect" "$work/home"
cp -R "$repo_root/tests/e2e/fixtures/environment/." "$work/source/"
chmod 0640 "$work/source/opt/lutra-demo/config/app.conf"
chmod 0750 "$work/source/opt/lutra-demo/data"

lutra="${LUTRA_E2E_LUTRA:-}"
if [[ -z "$lutra" ]]; then
  case "$(uname -m)" in
    arm64|aarch64) rid=linux-arm64 ;;
    x86_64|amd64) rid=linux-x64 ;;
    *) fail "unsupported Linux architecture: $(uname -m)" ;;
  esac
  publish="$work/publish"
  dotnet publish "$repo_root/src/Lutra.CLI/Lutra.CLI.csproj" -c Release -r "$rid" \
    --self-contained -p:PublishSingleFile=true -o "$publish"
  lutra="$publish/Lutra.CLI"
fi
[[ -x "$lutra" ]] || fail "Lutra executable is not available: $lutra"

config="$work/lutra.yaml"
write_config() {
  local include_volume="${1:-false}"
  {
    echo "backup_directory: $work/backups"
    echo "state_directory: $work/state"
    echo 'retention:'
    echo '  max_count: 3'
    echo '  max_age_days: 30'
    echo 'files:'
    echo '  - name: environment-files'
    echo '    paths:'
    echo "      - $work/source/etc/nginx/nginx.conf"
    echo "      - $work/source/etc/systemd/system/lutra-demo.service"
    echo "      - $work/source/opt/lutra-demo"
    echo '    schedule: daily'
    if [[ "$include_volume" == true ]]; then
      echo 'volumes:'
      echo '  - name: environment-volume'
      echo "    volume: $volume_name"
      echo '    schedule: daily'
    fi
    echo 'environment:'
    echo '  enabled: true'
    echo '  acknowledge_plaintext: true'
    if [[ "$include_volume" == true ]]; then
      echo '  targets: [environment-files, environment-volume]'
    else
      echo '  targets: [environment-files]'
    fi
    echo '  exclude: ["*.token"]'
    echo '  retention:'
    echo '    max_count: 2'
    echo '    max_age_days: 30'
    echo '    mode: both'
    echo '    keep_at_least: 1'
  } > "$config"
}

unprivileged_user=''
if (( EUID == 0 )); then
  unprivileged_user='lutra-e2e'
  id "$unprivileged_user" >/dev/null 2>&1 || useradd --system --home-dir "$work/home" "$unprivileged_user"
  chown -R "$unprivileged_user" "$work"
fi

run_unprivileged() {
  if [[ -n "$unprivileged_user" ]]; then
    runuser -u "$unprivileged_user" -- env HOME="$work/home" "$@"
  else
    "$@"
  fi
}

write_config false
run_unprivileged "$lutra" config validate --config "$config"
run_unprivileged "$lutra" environment backup --config "$config" | tee "$work/backup.out"

artifact="$(find "$work/backups/environment" -maxdepth 1 -type f -name 'environment_*.tar.gz' -print -quit)"
[[ -n "$artifact" ]] || fail 'environment artifact was not created'
[[ "$(stat -c '%a' "$work/backups/environment")" == 700 ]] || fail 'environment directory mode is not 0700'
[[ "$(stat -c '%a' "$artifact")" == 600 ]] || fail 'environment artifact mode is not 0600'

run_unprivileged "$lutra" environment inspect "$artifact" --config "$config" | tee "$work/inspect.out"
(
  cd "$(dirname "$artifact")"
  sha256sum --check "$(basename "$artifact").sha256"
)
tar -xzf "$artifact" -C "$work/inspect"
for member in manifest.json inventory/inventory.json inventory/inventory.md \
  payload/files/environment-files.tar.gz reports/backup.json MISSING_SECRETS.md RESTORE.md; do
  [[ -f "$work/inspect/$member" ]] || fail "missing recovery-set member: $member"
done
grep -q '"format_version": 1' "$work/inspect/manifest.json"
mkdir -p "$work/inspect/payload-contents"
tar -xzf "$work/inspect/payload/files/environment-files.tar.gz" -C "$work/inspect/payload-contents"
if grep -R -F "$sentinel" "$work/backups" "$work/state" "$work/inspect" \
    "$work/backup.out" "$work/inspect.out" >/dev/null 2>&1; then
  fail 'secret sentinel leaked into a recovery surface'
fi
if find "$work/inspect/payload-contents" -type f \( -name '.env' -o -name '*.token' \) | grep -q .; then
  fail 'a mandatory or configured secret path was archived'
fi
if find "$work/inspect/payload-contents" -type f \
    \( -name 'lutra.db' -o -name 'lutra.db-wal' -o -name 'lutra.db-shm' \) | grep -q .; then
  fail 'local Lutra state was archived'
fi

run_unprivileged "$lutra" environment restore "$artifact" --root "$work/restore" \
  --config "$config" | tee "$work/dry-run.out"
run_unprivileged "$lutra" environment restore "$artifact" --root "$work/restore" \
  --apply --yes --config "$config" | tee "$work/apply.out"

restored_source="$work/restore/${work#/}/source"
cmp "$work/source/etc/nginx/nginx.conf" "$restored_source/etc/nginx/nginx.conf"
cmp "$work/source/etc/systemd/system/lutra-demo.service" \
  "$restored_source/etc/systemd/system/lutra-demo.service"
cmp "$work/source/opt/lutra-demo/config/app.conf" \
  "$restored_source/opt/lutra-demo/config/app.conf"
cmp "$work/source/opt/lutra-demo/data/persistent.txt" \
  "$restored_source/opt/lutra-demo/data/persistent.txt"
[[ "$(stat -c '%a' "$restored_source/opt/lutra-demo/config/app.conf")" == 640 ]] \
  || fail 'application config mode was not restored'
[[ "$(stat -c '%a' "$restored_source/opt/lutra-demo/data")" == 750 ]] \
  || fail 'persistent data directory mode was not restored'

nginx -t -p "$work/restore/" -c "${work#/}/source/etc/nginx/nginx.conf"
systemd-analyze verify "$restored_source/etc/systemd/system/lutra-demo.service"

run_unprivileged "$lutra" environment restore "$artifact" --root "$work/restore" \
  --apply --yes --config "$config" | tee "$work/repeat.out"
grep -qi 'unchanged' "$work/repeat.out" || fail 'second restore did not report convergence'

if [[ "${LUTRA_E2E_VOLUME:-0}" == 1 ]]; then
  command -v docker >/dev/null || fail 'Docker CLI is required for the volume drill'
  docker info >/dev/null || fail 'Docker daemon is required for the volume drill'
  volume_name="lutra-e2e-$RANDOM-$$"
  cleanup_volume() { docker volume rm -f "$volume_name" >/dev/null 2>&1 || true; }
  trap cleanup_volume EXIT
  docker volume create "$volume_name" >/dev/null
  docker run --rm -v "$volume_name:/data" alpine sh -c 'printf original-volume-data > /data/persistent.txt'
  write_config true
  "$lutra" environment backup --config "$config" | tee "$work/volume-backup.out"
  volume_artifact="$(find "$work/backups/environment" -maxdepth 1 -type f -name 'environment_*.tar.gz' \
    -printf '%T@ %p\n' | sort -nr | head -1 | cut -d' ' -f2-)"
  docker run --rm -v "$volume_name:/data" alpine sh -c 'printf changed-after-backup > /data/persistent.txt'
  "$lutra" environment restore "$volume_artifact" --root "$work/restore" \
    --include-volumes --apply --yes --config "$config" | tee "$work/volume-restore.out"
  restored_volume="$(docker run --rm -v "$volume_name:/data:ro" alpine cat /data/persistent.txt)"
  [[ "$restored_volume" == original-volume-data ]] || fail 'Docker volume content was not restored'
  mkdir -p "$work/volume-inspect/payload-contents"
  tar -xzf "$volume_artifact" -C "$work/volume-inspect"
  tar -xzf "$work/volume-inspect/payload/files/environment-files.tar.gz" \
    -C "$work/volume-inspect/payload-contents"
  if grep -R -F "$sentinel" "$work/volume-inspect" "$work/state/logs" \
      "$work/volume-backup.out" "$work/volume-restore.out" >/dev/null 2>&1; then
    fail 'secret sentinel leaked during the volume recovery drill'
  fi
  cleanup_volume
  trap - EXIT
fi

echo 'Environment recovery drill passed.'
