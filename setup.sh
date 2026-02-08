#!/bin/bash
set -e

# Lutra Setup Script
# Automates installation and initial configuration

BOLD='\033[1m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
RED='\033[0;31m'
NC='\033[0m' # No Color

echo -e "${BOLD}🦦 Lutra Setup${NC}"
echo ""

# Check if running as root
if [ "$EUID" -eq 0 ]; then
    INSTALL_DIR="/usr/local/bin"
    CONFIG_DIR="/etc/lutra"
    BACKUP_DIR="/var/backups/lutra"
    INSTALL_USER=$(logname 2>/dev/null || echo $SUDO_USER)
else
    INSTALL_DIR="$HOME/.local/bin"
    CONFIG_DIR="$HOME/.config/lutra"
    BACKUP_DIR="$HOME/backups/lutra"
    INSTALL_USER=$USER
fi

echo -e "${BLUE}Installation type:${NC} $([ "$EUID" -eq 0 ] && echo "System-wide (requires sudo)" || echo "User-only")"
echo -e "${BLUE}Binary location:${NC} $INSTALL_DIR/lutra"
echo -e "${BLUE}Config directory:${NC} $CONFIG_DIR"
echo -e "${BLUE}Backup directory:${NC} $BACKUP_DIR"
echo ""

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

# Check .NET (for building)
if ! command -v dotnet &> /dev/null; then
    echo -e "${YELLOW}⚠ .NET SDK not found (needed to build from source)${NC}"
else
    echo -e "${GREEN}✓ .NET SDK found${NC}"
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

# Build and install binary
echo -e "${BOLD}Building Lutra...${NC}"

if ! command -v dotnet &> /dev/null; then
    echo -e "${RED}✗ Cannot build: .NET SDK not found${NC}"
    exit 1
fi

dotnet publish src/Lutra.CLI/Lutra.CLI.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained \
    -p:PublishSingleFile=true \
    -o dist/ \
    > /dev/null 2>&1

if [ $? -ne 0 ]; then
    echo -e "${RED}✗ Build failed${NC}"
    exit 1
fi

echo -e "${GREEN}✓ Build successful${NC}"

# Install binary
echo -e "${BOLD}Installing binary...${NC}"
cp dist/Lutra.CLI "$INSTALL_DIR/lutra"
chmod +x "$INSTALL_DIR/lutra"
echo -e "${GREEN}✓ Installed to $INSTALL_DIR/lutra${NC}"
echo ""

# Create template configuration
echo -e "${BOLD}Creating template configuration...${NC}"

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

# Create template .env file
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
echo ""

# Add to PATH if needed (user install only)
if [ "$EUID" -ne 0 ]; then
    if [[ ":$PATH:" != *":$INSTALL_DIR:"* ]]; then
        echo -e "${YELLOW}⚠ $INSTALL_DIR is not in your PATH${NC}"
        echo ""
        echo "Add this to your ~/.bashrc or ~/.zshrc:"
        echo -e "${BLUE}export PATH=\"\$HOME/.local/bin:\$PATH\"${NC}"
        echo ""
        echo "Then reload your shell:"
        echo -e "${BLUE}source ~/.bashrc${NC}  # or: source ~/.zshrc"
        echo ""
    fi
fi

# Success summary
echo -e "${BOLD}${GREEN}✓ Installation complete!${NC}"
echo ""
echo -e "${BOLD}Next steps:${NC}"
echo ""
echo -e "1. ${BOLD}Edit configuration:${NC}"
echo -e "   ${BLUE}nano $CONFIG_DIR/lutra.yaml${NC}"
echo -e "   - Update container names, database names, etc."
echo -e "   - Remove example entries you don't need"
echo ""
echo -e "2. ${BOLD}Set credentials:${NC}"
echo -e "   ${BLUE}nano $CONFIG_DIR/.env${NC}"
echo -e "   - Replace placeholder passwords with real values"
echo ""
echo -e "3. ${BOLD}Validate configuration:${NC}"
echo -e "   ${BLUE}lutra config validate --config $CONFIG_DIR/lutra.yaml --env-file $CONFIG_DIR/.env${NC}"
echo ""
echo -e "4. ${BOLD}Run your first backup:${NC}"
echo -e "   ${BLUE}lutra backup run --config $CONFIG_DIR/lutra.yaml --env-file $CONFIG_DIR/.env${NC}"
echo ""
echo -e "5. ${BOLD}Set up automated backups (optional):${NC}"
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