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
FROM_RELEASE=false
NO_MODIFY_PATH=false

usage() {
    echo "Usage: setup.sh [OPTIONS]"
    echo ""
    echo "Install and configure Lutra backup tool."
    echo ""
    echo "Options:"
    echo "  --from-release     Download pre-built binary from GitHub releases"
    echo "                     instead of building from source"
    echo "  --no-modify-path   Skip automatic PATH modification"
    echo "  -h, --help         Show this help message"
    echo ""
    echo "Installation modes:"
    echo "  User install:     bash setup.sh          (~/.local/bin/lutra)"
    echo "  System install:   sudo bash setup.sh     (/usr/local/bin/lutra)"
    echo ""
    echo "If .NET SDK is not found, the script automatically downloads"
    echo "a pre-built binary from GitHub releases."
}

for arg in "$@"; do
    case "$arg" in
        --from-release) FROM_RELEASE=true ;;
        --no-modify-path) NO_MODIFY_PATH=true ;;
        -h|--help) usage; exit 0 ;;
        *)
            echo "Unknown option: $arg"
            usage
            exit 1
            ;;
    esac
done

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
    if command -v lutra &> /dev/null; then
        INSTALLED_VERSION=$(lutra --version 2>/dev/null || echo "")
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

# Detect user's shell and add INSTALL_DIR to PATH in the appropriate config file
modify_shell_path() {
    local install_dir="$1"
    local shell_name
    shell_name=$(basename "${SHELL:-/bin/bash}")

    local config_file=""
    local path_line=""

    case "$shell_name" in
        bash)
            config_file="$HOME/.bashrc"
            path_line="export PATH=\"$install_dir:\$PATH\""
            ;;
        zsh)
            config_file="$HOME/.zshrc"
            path_line="export PATH=\"$install_dir:\$PATH\""
            ;;
        fish)
            config_file="${XDG_CONFIG_HOME:-$HOME/.config}/fish/config.fish"
            path_line="fish_add_path $install_dir"
            ;;
        *)
            echo -e "${YELLOW}⚠ Unsupported shell for automatic PATH setup: $shell_name${NC}"
            echo "  Add $install_dir to your PATH manually."
            echo ""
            return 0
            ;;
    esac

    # Deduplication: skip if already present
    if [ -f "$config_file" ] && grep -qF "$install_dir" "$config_file"; then
        echo -e "${BLUE}ℹ $config_file already contains $install_dir${NC}"
        echo ""
        return 0
    fi

    echo -e "${YELLOW}⚠ $install_dir is not in your PATH${NC}"
    echo ""

    if [ "$NO_MODIFY_PATH" = true ]; then
        echo "  Add this to your $config_file:"
        echo -e "  ${BLUE}${path_line}${NC}"
        echo ""
        return 0
    fi

    read -p "  Add $install_dir to PATH in $config_file? [Y/n] " REPLY
    REPLY="${REPLY:-Y}"

    if [[ "$REPLY" =~ ^[Yy]$ ]]; then
        echo "" >> "$config_file"
        echo "# Added by Lutra setup" >> "$config_file"
        echo "$path_line" >> "$config_file"
        echo -e "  ${GREEN}✓ Updated $config_file${NC}"
        echo -e "  Run ${BLUE}source $config_file${NC} or restart your shell to apply."
    else
        echo "  Add this to your $config_file:"
        echo -e "  ${BLUE}${path_line}${NC}"
    fi
    echo ""
}

echo -e "${BOLD}🦦 Lutra Setup${NC}"
echo ""

# Check if running as root
if [ "$EUID" -eq 0 ]; then
    INSTALL_DIR="/usr/local/bin"
    CONFIG_DIR="/etc/lutra"
    BACKUP_DIR="/var/backups/lutra"
    INSTALL_USER=$(logname 2>/dev/null || echo "${SUDO_USER:-root}")
else
    INSTALL_DIR="$HOME/.local/bin"
    CONFIG_DIR="$HOME/.config/lutra"
    BACKUP_DIR="$HOME/backups/lutra"
    INSTALL_USER="$USER"
fi

detect_alternate_installation() {
    ALTERNATE_BINARY=""
    ALTERNATE_LOCATION=""

    if [ "$EUID" -eq 0 ]; then
        if [ -n "$INSTALL_USER" ] && [ "$INSTALL_USER" != "root" ]; then
            USER_HOME=$(eval echo ~"$INSTALL_USER")
            ALTERNATE_BINARY="$USER_HOME/.local/bin/lutra"
            ALTERNATE_LOCATION="user installation ($USER_HOME/.local/bin)"
        fi
    else
        ALTERNATE_BINARY="/usr/local/bin/lutra"
        ALTERNATE_LOCATION="system-wide installation (/usr/local/bin)"
    fi

    [ -n "$ALTERNATE_BINARY" ] && [ -f "$ALTERNATE_BINARY" ]
}

echo -e "${BLUE}Installation type:${NC} $([ "$EUID" -eq 0 ] && echo "System-wide (requires sudo)" || echo "User-only")"
echo -e "${BLUE}Binary location:${NC} $INSTALL_DIR/lutra"
echo -e "${BLUE}Config directory:${NC} $CONFIG_DIR"
echo -e "${BLUE}Backup directory:${NC} $BACKUP_DIR"
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
    echo "    [2] Abort (re-run with$([ "$EUID" -eq 0 ] && echo "out" || echo "") sudo to update existing installation)"
    echo "    [3] Continue anyway"
    echo ""
    read -p "  Enter choice [1-3]: " CHOICE

    case "$CHOICE" in
        1)
            if [ "$EUID" -eq 0 ]; then
                rm -f "$ALTERNATE_BINARY"
            else
                sudo rm -f "$ALTERNATE_BINARY"
            fi
            echo -e "  ${GREEN}✓ Removed $ALTERNATE_BINARY${NC}"
            echo ""
            ;;
        2)
            echo ""
            if [ "$EUID" -eq 0 ]; then
                echo -e "  To update the existing $ALTERNATE_LOCATION, re-run ${BOLD}without${NC} sudo:"
                echo -e "    ${BLUE}bash setup.sh${NC}"
            else
                echo -e "  To update the existing $ALTERNATE_LOCATION, re-run ${BOLD}with${NC} sudo:"
                echo -e "    ${BLUE}sudo bash setup.sh${NC}"
            fi
            echo ""
            exit 0
            ;;
        3)
            echo -e "  ${YELLOW}Continuing with dual installation...${NC}"
            echo ""
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
if ! command -v docker &> /dev/null; then
    echo -e "${RED}✗ Docker not found${NC}"
    echo "  Install Docker first: https://docs.docker.com/engine/install/"
    exit 1
else
    echo -e "${GREEN}✓ Docker found${NC}"
fi

# Check if user can run docker
if ! docker ps &> /dev/null; then
    echo -e "${YELLOW}⚠ Cannot run Docker commands${NC}"
    echo "  Add user to docker group: sudo usermod -aG docker $USER"
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

if [ "$EUID" -eq 0 ]; then
    mkdir -p "$INSTALL_DIR"
    mkdir -p "$CONFIG_DIR"
    mkdir -p "$BACKUP_DIR"
    chown -R "$INSTALL_USER:$INSTALL_USER" "$CONFIG_DIR"
    chown -R "$INSTALL_USER:$INSTALL_USER" "$BACKUP_DIR"
else
    mkdir -p "$INSTALL_DIR"
    mkdir -p "$CONFIG_DIR"
    mkdir -p "$BACKUP_DIR"
fi

echo -e "${GREEN}✓ Directories created${NC}"
echo ""

# Build or download binary
if [ "$FROM_RELEASE" = true ] || [ "$HAS_DOTNET" = false ]; then
    install_from_release
else
    # Version check: skip if already installed at the same version
    SKIP_BUILD=false
    CSPROJ_VERSION=$(grep '<Version>' src/Lutra.CLI/Lutra.CLI.csproj 2>/dev/null \
        | sed 's/.*<Version>\(.*\)<\/Version>.*/\1/' || echo "")
    if [ -n "$CSPROJ_VERSION" ] && command -v lutra &> /dev/null; then
        INSTALLED_VERSION=$(lutra --version 2>/dev/null || echo "")
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
    schedule: "0 3 * * *"           # Daily at 3 AM
    format: custom                   # custom (.dump) or plain (.sql)
    compression: gzip

  # MongoDB Example
  # - name: example-mongo
  #   type: mongodb
  #   container: mongo-container
  #   database: mydb
  #   schedule: "0 4 * * 0"         # Weekly on Sundays at 4 AM
  #   compression: gzip

  # SQL Server Example
  # - name: example-sqlserver
  #   type: sqlserver
  #   container: sqlserver-container
  #   database: MyDatabase
  #   username: sa
  #   password_env: SQLSERVER_PASSWORD
  #   schedule: "0 2 * * *"         # Daily at 2 AM
  #   compression: gzip
EOF

    # Replace placeholder with actual backup directory
    if [ "$(uname)" = "Darwin" ]; then
        sed -i '' "s|BACKUP_DIR_PLACEHOLDER|$BACKUP_DIR|g" "$CONFIG_DIR/lutra.yaml"
    else
        sed -i "s|BACKUP_DIR_PLACEHOLDER|$BACKUP_DIR|g" "$CONFIG_DIR/lutra.yaml"
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

# Add to PATH if needed (user install only)
if [ "$EUID" -ne 0 ]; then
    if [[ ":$PATH:" != *":$INSTALL_DIR:"* ]]; then
        modify_shell_path "$INSTALL_DIR"
    fi
fi

# Success summary
echo -e "${BOLD}${GREEN}✓ Installation complete!${NC}"
echo ""
echo -e "${BOLD}Next steps:${NC}"
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
if [ "$EUID" -eq 0 ]; then
    echo -e "   ${BLUE}lutra schedule install --config $CONFIG_DIR/lutra.yaml --env-file $CONFIG_DIR/.env${NC}"
    echo -e "   ${BLUE}systemctl daemon-reload${NC}"
    echo -e "   ${BLUE}systemctl enable --now lutra-backup-*.timer${NC}"
else
    echo -e "   ${BLUE}sudo lutra schedule install --config $CONFIG_DIR/lutra.yaml --env-file $CONFIG_DIR/.env${NC}"
fi
echo ""
echo -e "${BOLD}Documentation:${NC} https://github.com/gustmrg/lutra"
echo ""
