#!/bin/bash
set -euo pipefail

# Lutra Setup Script
# Automates installation and initial configuration

BOLD='\033[1m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
RED='\033[0;31m'
NC='\033[0m' # No Color

GITHUB_REPO="gustmrg/lutra"
FROM_RELEASE=true
FROM_SOURCE=false
RUN_AS_USER=""

usage() {
    echo "Usage: setup.sh [OPTIONS]"
    echo ""
    echo "Install and configure Lutra backup tool."
    echo ""
    echo "Options:"
    echo "  --from-release     Download pre-built binary from GitHub releases (default)"
    echo "  --from-source      Build and install from the local source checkout"
    echo "  --run-as USER      Non-root account that will run Lutra (defaults to SUDO_USER)"
    echo "  -h, --help         Show this help message"
    echo ""
    echo "Lutra supports system-wide installation only. Run this script with sudo."
    echo "The binary, configuration, and systemd units are machine-wide, but services run as USER."
    echo "The default installation downloads a pre-built binary from GitHub releases."
    echo "Use --from-source only when running from a source checkout."
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --from-release) FROM_RELEASE=true; FROM_SOURCE=false ;;
        --from-source) FROM_SOURCE=true; FROM_RELEASE=false ;;
        --run-as)
            if [ "$#" -lt 2 ]; then
                echo "--run-as requires a user name"
                exit 1
            fi
            RUN_AS_USER="$2"
            shift
            ;;
        -h|--help) usage; exit 0 ;;
        *)
            echo "Unknown option: $1"
            usage
            exit 1
            ;;
    esac
    shift
done

if [ "$EUID" -ne 0 ]; then
    echo -e "${RED}✗ Lutra supports system-wide installation only.${NC}"
    echo "  Re-run this setup command with sudo."
    exit 1
fi

# PID-scoped temp directory to prevent collisions
TMPDIR_INSTALL="/tmp/lutra_install_$$"
cleanup_tmpdir() {
    rm -rf "$TMPDIR_INSTALL"
}
trap cleanup_tmpdir EXIT

install_from_release() {
    echo -e "${BOLD}Downloading latest release...${NC}"

    ARCH=$(uname -m)
    case "$ARCH" in
        x86_64)  RID="linux-x64" ;;
        aarch64) RID="linux-arm64" ;;
        *)
            echo -e "${RED}✗ Unsupported architecture: $ARCH${NC}"
            exit 1
            ;;
    esac

    TAG=$(curl -s "https://api.github.com/repos/${GITHUB_REPO}/releases/latest" \
        | grep '"tag_name"' | cut -d'"' -f4)

    if [ -z "$TAG" ]; then
        echo -e "${RED}✗ Could not determine latest release${NC}"
        echo "  Check https://github.com/${GITHUB_REPO}/releases"
        exit 1
    fi

    echo -e "${BLUE}Latest release:${NC} $TAG ($RID)"

    # Skip if the same version is already installed
    if [ -x "$INSTALL_DIR/lutra" ]; then
        INSTALLED_VERSION=$("$INSTALL_DIR/lutra" --version 2>/dev/null || echo "")
        if [ -n "$INSTALLED_VERSION" ] && [[ "$INSTALLED_VERSION" == *"${TAG#v}"* ]]; then
            echo -e "${GREEN}✓ Lutra $TAG is already installed — skipping download${NC}"
            return 0
        fi
    fi

    TARBALL="lutra-${RID}.tar.gz"
    BASE_URL="https://github.com/${GITHUB_REPO}/releases/download/${TAG}"

    # Verify release asset exists before downloading
    HTTP_STATUS=$(curl -sI -o /dev/null -w "%{http_code}" "${BASE_URL}/${TARBALL}")
    if [ "$HTTP_STATUS" = "404" ] || [ "$HTTP_STATUS" = "000" ]; then
        echo -e "${RED}✗ Release asset not found: ${TARBALL}${NC}"
        echo "  URL: ${BASE_URL}/${TARBALL}"
        echo "  HTTP status: $HTTP_STATUS"
        echo "  Check https://github.com/${GITHUB_REPO}/releases/tag/${TAG}"
        exit 1
    fi

    mkdir -p "$TMPDIR_INSTALL"

    curl -sL "${BASE_URL}/${TARBALL}" -o "${TMPDIR_INSTALL}/${TARBALL}"
    curl -sL "${BASE_URL}/${TARBALL}.sha256" -o "${TMPDIR_INSTALL}/${TARBALL}.sha256"

    echo -e "${BOLD}Verifying checksum...${NC}"
    if ! (cd "$TMPDIR_INSTALL" && sha256sum -c "${TARBALL}.sha256"); then
        echo -e "${RED}✗ Checksum verification failed${NC}"
        exit 1
    fi

    echo -e "${GREEN}✓ Checksum verified${NC}"

    tar -xzf "${TMPDIR_INSTALL}/${TARBALL}" -C "$TMPDIR_INSTALL"
    cp "${TMPDIR_INSTALL}/lutra" "$INSTALL_DIR/lutra"
    chmod +x "$INSTALL_DIR/lutra"

    echo -e "${GREEN}✓ Installed to $INSTALL_DIR/lutra${NC}"
}

echo -e "${BOLD}🦦 Lutra Setup${NC}"
echo ""

INSTALL_DIR="/usr/local/bin"
CONFIG_DIR="/etc/lutra"
BACKUP_DIR="/var/backups/lutra"
STATE_DIR="/var/lib/lutra"

if [ -z "$RUN_AS_USER" ]; then
    RUN_AS_USER="${SUDO_USER:-}"
fi
if [ -z "$RUN_AS_USER" ] || [ "$RUN_AS_USER" = root ]; then
    echo -e "${RED}✗ A non-root runtime user is required.${NC}"
    echo "  Run through sudo from that account, or pass --run-as USER."
    exit 1
fi
if ! id "$RUN_AS_USER" >/dev/null 2>&1; then
    echo -e "${RED}✗ Runtime user does not exist: $RUN_AS_USER${NC}"
    exit 1
fi
RUN_AS_GROUP=$(id -gn "$RUN_AS_USER")
RUN_AS_HOME=$(getent passwd "$RUN_AS_USER" | cut -d: -f6)
if [ -z "$RUN_AS_HOME" ] || [[ "$RUN_AS_HOME" != /* ]]; then
    echo -e "${RED}✗ Runtime user does not have an absolute home directory: $RUN_AS_USER${NC}"
    exit 1
fi

detect_alternate_installation() {
    ALTERNATE_BINARY=""

    ALTERNATE_BINARY="$RUN_AS_HOME/.local/bin/lutra"

    [ -n "$ALTERNATE_BINARY" ] && [ -f "$ALTERNATE_BINARY" ]
}

echo -e "${BLUE}Installation type:${NC} System-wide"
echo -e "${BLUE}Runtime user:${NC} $RUN_AS_USER"
echo -e "${BLUE}Binary location:${NC} $INSTALL_DIR/lutra"
echo -e "${BLUE}Config directory:${NC} $CONFIG_DIR"
echo -e "${BLUE}Backup directory:${NC} $BACKUP_DIR"
echo -e "${BLUE}State directory:${NC} $STATE_DIR"
echo ""

# Check for existing installation in alternate location
if detect_alternate_installation; then
    echo -e "${YELLOW}⚠ WARNING: Existing Lutra installation detected${NC}"
    echo -e "  Found binary at: ${BOLD}$ALTERNATE_BINARY${NC}"
    echo -e "  This setup will install to: ${BOLD}$INSTALL_DIR/lutra${NC}"
    echo ""
    echo -e "  ${BOLD}This will result in TWO separate installations.${NC}"
    echo -e "  The one with higher \$PATH priority will be used, which may not be the newest."
    echo ""
    echo "  Choose an option:"
    echo "    [1] Remove old installation and continue (recommended)"
    echo "    [2] Abort and remove it manually"
    echo ""
    read -p "  Enter choice [1-2]: " CHOICE

    case "$CHOICE" in
        1)
            rm -f "$ALTERNATE_BINARY"
            echo -e "  ${GREEN}✓ Removed $ALTERNATE_BINARY${NC}"
            echo ""
            ;;
        2)
            echo ""
            echo "  User installations are no longer supported."
            echo -e "  Re-run this system installer and choose option 1 to remove the old binary."
            echo ""
            exit 0
            ;;
        *)
            echo -e "  ${RED}Invalid choice. Aborting.${NC}"
            exit 1
            ;;
    esac
fi

# Check prerequisites
echo -e "${BOLD}Checking prerequisites...${NC}"

# Check Docker
DOCKER_AVAILABLE=false
if ! command -v docker &> /dev/null; then
    echo -e "${YELLOW}⚠ Docker not found${NC}"
    echo "  Database and volume targets require Docker."
    echo "  Install Docker: https://docs.docker.com/engine/install/"
else
    DOCKER_AVAILABLE=true
    echo -e "${GREEN}✓ Docker found${NC}"
fi

# Check if the runtime user can run Docker
if [ "$DOCKER_AVAILABLE" = true ] && ! runuser -u "$RUN_AS_USER" -- docker ps &> /dev/null; then
    echo -e "${YELLOW}⚠ Runtime user cannot run Docker commands${NC}"
    echo "  Add user to docker group: sudo usermod -aG docker $RUN_AS_USER"
    echo "  Then log out and back in."
fi

# Check .NET (for building from source)
HAS_DOTNET=false
if command -v dotnet &> /dev/null; then
    HAS_DOTNET=true
    echo -e "${GREEN}✓ .NET SDK found${NC}"
else
    echo -e "${YELLOW}⚠ .NET SDK not found — will download pre-built binary${NC}"
fi

echo ""

# Create directories
echo -e "${BOLD}Creating directories...${NC}"

mkdir -p "$INSTALL_DIR"
mkdir -p "$CONFIG_DIR"
mkdir -p "$BACKUP_DIR"
mkdir -p "$STATE_DIR"

echo -e "${GREEN}✓ Directories created${NC}"
echo ""

# Build or download binary
if [ "$FROM_RELEASE" = true ]; then
    install_from_release
else
    if [ "$HAS_DOTNET" = false ]; then
        echo -e "${RED}✗ .NET SDK is required for --from-source${NC}"
        echo "  Install the .NET 10 SDK or omit --from-source to install the latest release."
        exit 1
    fi

    if [ ! -f "src/Lutra.CLI/Lutra.CLI.csproj" ]; then
        echo -e "${RED}✗ Source checkout not found${NC}"
        echo "  Run this command from the Lutra repository, or omit --from-source to install the latest release."
        exit 1
    fi

    # Version check: skip if already installed at the same version
    SKIP_BUILD=false
    CSPROJ_VERSION=$(grep '<Version>' src/Lutra.CLI/Lutra.CLI.csproj 2>/dev/null \
        | sed 's/.*<Version>\(.*\)<\/Version>.*/\1/' || echo "")
    if [ -n "$CSPROJ_VERSION" ] && [ -x "$INSTALL_DIR/lutra" ]; then
        INSTALLED_VERSION=$("$INSTALL_DIR/lutra" --version 2>/dev/null || echo "")
        if [ -n "$INSTALLED_VERSION" ] && [[ "$INSTALLED_VERSION" == *"$CSPROJ_VERSION"* ]]; then
            echo -e "${GREEN}✓ Lutra $CSPROJ_VERSION is already installed — skipping build${NC}"
            SKIP_BUILD=true
        fi
    fi

    if [ "$SKIP_BUILD" = false ]; then
        echo -e "${BOLD}Building Lutra...${NC}"

        if ! dotnet publish src/Lutra.CLI/Lutra.CLI.csproj \
            -c Release \
            -r linux-x64 \
            --self-contained \
            -p:PublishSingleFile=true \
            -o dist/ \
            > /dev/null 2>&1; then
            echo -e "${RED}✗ Build failed${NC}"
            exit 1
        fi

        echo -e "${GREEN}✓ Build successful${NC}"

        cp dist/Lutra.CLI "$INSTALL_DIR/lutra"
        chmod +x "$INSTALL_DIR/lutra"
        echo -e "${GREEN}✓ Installed to $INSTALL_DIR/lutra${NC}"
    fi
fi
chown root:root "$INSTALL_DIR/lutra"
chmod 0755 "$INSTALL_DIR/lutra"
echo ""

# Create template configuration
echo -e "${BOLD}Creating template configuration...${NC}"

CONFIG_CREATED=false

if [ -f "$CONFIG_DIR/lutra.yaml" ]; then
    echo -e "${BLUE}ℹ Preserving existing config: $CONFIG_DIR/lutra.yaml${NC}"
else
    cat > "$CONFIG_DIR/lutra.yaml" <<'EOF'
# Lutra Configuration File
# Documentation: https://github.com/gustmrg/lutra

backup_directory: BACKUP_DIR_PLACEHOLDER
state_directory: STATE_DIR_PLACEHOLDER

retention:
  max_count: 10        # Keep at most 10 backups per target
  max_age_days: 30     # Delete backups older than 30 days (when max_count also exceeded)

databases:
  # PostgreSQL Example
  - name: example-postgres
    type: postgresql
    container: postgres-container    # Docker container name
    database: mydb                   # Database name inside container
    username: postgres
    password_env: POSTGRES_PASSWORD  # Reference to env var in .env file
    schedule: "*-*-* 03:00:00"      # Daily at 3 AM (systemd calendar expression)
    format: custom                   # custom (.dump) or plain (.sql)
    compression: gzip

  # MongoDB Example
  # - name: example-mongo
  #   type: mongodb
  #   container: mongo-container
  #   database: mydb
  #   schedule: "Sun *-*-* 04:00:00" # Weekly on Sundays at 4 AM
  #   compression: gzip

  # SQL Server Example
  # - name: example-sqlserver
  #   type: sqlserver
  #   container: sqlserver-container
  #   database: MyDatabase
  #   username: sa
  #   password_env: SQLSERVER_PASSWORD
  #   schedule: "*-*-* 02:00:00"    # Daily at 2 AM
  #   compression: gzip

# Optional plaintext environment recovery. Common secret paths in file targets
# are always excluded; volume contents cannot be classified. Restore secrets
# externally, do not select credential-store volumes, and use restricted storage.
# Built-in sync excludes these sets.
# environment:
#   enabled: true
#   acknowledge_plaintext: true
#   schedule: "Sun *-*-* 01:00:00"
#   targets: [app-config, app-uploads]
#   systemd_units: [nginx.service, myapp.service]
#   docker_containers: [myapp]
EOF

    # Replace placeholders with actual data directories
    if [ "$(uname)" = "Darwin" ]; then
        sed -i '' "s|BACKUP_DIR_PLACEHOLDER|$BACKUP_DIR|g" "$CONFIG_DIR/lutra.yaml"
        sed -i '' "s|STATE_DIR_PLACEHOLDER|$STATE_DIR|g" "$CONFIG_DIR/lutra.yaml"
    else
        sed -i "s|BACKUP_DIR_PLACEHOLDER|$BACKUP_DIR|g" "$CONFIG_DIR/lutra.yaml"
        sed -i "s|STATE_DIR_PLACEHOLDER|$STATE_DIR|g" "$CONFIG_DIR/lutra.yaml"
    fi

    echo -e "${GREEN}✓ Created $CONFIG_DIR/lutra.yaml${NC}"
    CONFIG_CREATED=true
fi

if [ -f "$CONFIG_DIR/.env" ]; then
    echo -e "${BLUE}ℹ Preserving existing env file: $CONFIG_DIR/.env${NC}"
else
    cat > "$CONFIG_DIR/.env" <<'EOF'
# Lutra Environment Variables
# Store database passwords here (never commit this file!)

# Example PostgreSQL password
POSTGRES_PASSWORD=your-secret-password-here

# Example MongoDB password (if authentication is enabled)
# MONGO_PASSWORD=your-mongo-password

# Example SQL Server password
# SQLSERVER_PASSWORD=your-sqlserver-password
EOF

    chmod 600 "$CONFIG_DIR/.env"
    echo -e "${GREEN}✓ Created $CONFIG_DIR/.env (mode 600)${NC}"
fi

echo ""

# Make the runtime identity consistent for configuration, artifacts, and SQLite state.
chmod 600 "$CONFIG_DIR/.env"
chown -R "$RUN_AS_USER:$RUN_AS_GROUP" "$CONFIG_DIR" "$BACKUP_DIR" "$STATE_DIR"

# Success summary
echo -e "${BOLD}${GREEN}✓ Installation complete!${NC}"
echo ""
echo -e "${BOLD}Next steps:${NC}"
echo -e "Run Lutra commands as ${BOLD}$RUN_AS_USER${NC}; use sudo only for installation and systemd management."
echo ""

STEP=1

if [ "$CONFIG_CREATED" = true ]; then
    echo -e "${STEP}. ${BOLD}Edit configuration:${NC}"
    echo -e "   ${BLUE}nano $CONFIG_DIR/lutra.yaml${NC}"
    echo -e "   - Update container names, database names, etc."
    echo -e "   - Remove example entries you don't need"
    echo ""
    STEP=$((STEP + 1))
    echo -e "${STEP}. ${BOLD}Set credentials:${NC}"
    echo -e "   ${BLUE}nano $CONFIG_DIR/.env${NC}"
    echo -e "   - Replace placeholder passwords with real values"
    echo ""
    STEP=$((STEP + 1))
else
    echo -e "${BLUE}ℹ Using existing configuration files${NC}"
    echo -e "   Config: $CONFIG_DIR/lutra.yaml"
    echo -e "   Env:    $CONFIG_DIR/.env"
    echo ""
fi

echo -e "${STEP}. ${BOLD}Validate configuration:${NC}"
echo -e "   ${BLUE}lutra config validate --config $CONFIG_DIR/lutra.yaml --env-file $CONFIG_DIR/.env${NC}"
echo ""
STEP=$((STEP + 1))
echo -e "${STEP}. ${BOLD}Run your first backup:${NC}"
echo -e "   ${BLUE}lutra backup run --config $CONFIG_DIR/lutra.yaml --env-file $CONFIG_DIR/.env${NC}"
echo ""
STEP=$((STEP + 1))
echo -e "${STEP}. ${BOLD}Set up automated backups (optional):${NC}"
echo -e "   ${BLUE}sudo lutra schedule install --run-as $RUN_AS_USER --config $CONFIG_DIR/lutra.yaml --env-file $CONFIG_DIR/.env${NC}"
echo -e "   ${BLUE}sudo systemctl daemon-reload${NC}"
echo -e "   ${BLUE}sudo systemctl enable --now lutra-backup-*.timer${NC}"
echo ""
echo -e "${BOLD}Documentation:${NC} https://github.com/gustmrg/lutra"
echo ""
