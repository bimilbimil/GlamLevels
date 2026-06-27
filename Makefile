PROJECT_NAME = GlamLevels
CSPROJ       = $(PROJECT_NAME).csproj
DLL_NAME     = $(PROJECT_NAME).dll
JSON_NAME    = $(PROJECT_NAME).json
YAML_NAME    = $(PROJECT_NAME).yaml

BUILD_DIR  = bin/Debug
BUILD_DLL  = $(BUILD_DIR)/$(DLL_NAME)
BUILD_JSON = $(BUILD_DIR)/$(JSON_NAME)

PLUGIN_DIR  = ~/Library/Application\ Support/XIV\ on\ Mac/dalamud/Hooks/dev/plugins
PLUGIN_DLL  = $(PLUGIN_DIR)/$(DLL_NAME)
PLUGIN_JSON = $(PLUGIN_DIR)/$(JSON_NAME)

CONFIGURATION = Debug

.PHONY: all build clean deploy rebuild build-only package help

all: build

build: $(BUILD_DLL) deploy
	@echo "Build and deployment complete."
	@echo "  DLL: $(BUILD_DLL)"
	@echo "  Installed to: $(PLUGIN_DIR)"

$(BUILD_DLL): $(CSPROJ) $(wildcard **/*.cs)
	dotnet build -c $(CONFIGURATION)
	@test -f $(BUILD_DLL) || (echo "Build failed — DLL not found" && exit 1)

deploy: $(BUILD_DLL) $(PLUGIN_DIR)
	@cp $(BUILD_DLL) $(PLUGIN_DLL)
	@cp $(JSON_NAME) $(PLUGIN_JSON)
	@echo "Deployed $(DLL_NAME) and $(JSON_NAME)"

$(PLUGIN_DIR):
	@mkdir -p $(PLUGIN_DIR)

clean:
	dotnet clean

rebuild: clean build

build-only:
	dotnet build -c $(CONFIGURATION)

# Usage: make package [RELEASE_TAG=v1.0.0]
package: $(BUILD_DLL)
	@echo "Creating release package..."
	@TIMESTAMP=$$(date +%s); \
	if command -v jq >/dev/null 2>&1; then \
		CURRENT=$$(jq -r '.[0].AssemblyVersion' repo.json); \
		MAJOR=$$(echo $$CURRENT | cut -d. -f1); \
		MINOR=$$(echo $$CURRENT | cut -d. -f2); \
		PATCH=$$(echo $$CURRENT | cut -d. -f3); \
		BUILD=$$(echo $$CURRENT | cut -d. -f4); \
		NEW_BUILD=$$((BUILD + 1)); \
		NEW_VER="$$MAJOR.$$MINOR.$$PATCH.$$NEW_BUILD"; \
		REPO_URL=$$(jq -r '.[0].RepoUrl' repo.json); \
		if [ -n "$$RELEASE_TAG" ]; then \
			DL_URL="$$REPO_URL/releases/download/$$RELEASE_TAG/$(PROJECT_NAME).zip"; \
			jq --arg ts $$TIMESTAMP --arg v $$NEW_VER --arg u $$DL_URL \
				'.[0].LastUpdate = ($$ts | tonumber) | .[0].AssemblyVersion = $$v | .[0].DownloadLinkInstall = $$u | .[0].DownloadLinkUpdate = $$u | .[0].DownloadLinkTesting = $$u' \
				repo.json > repo.json.tmp && mv repo.json.tmp repo.json; \
		else \
			jq --arg ts $$TIMESTAMP --arg v $$NEW_VER \
				'.[0].LastUpdate = ($$ts | tonumber) | .[0].AssemblyVersion = $$v' \
				repo.json > repo.json.tmp && mv repo.json.tmp repo.json; \
		fi; \
		jq --arg v $$NEW_VER '.AssemblyVersion = $$v' $(JSON_NAME) > $(JSON_NAME).tmp && mv $(JSON_NAME).tmp $(JSON_NAME); \
		sed -i.bak "s/\"AssemblyVersion\": \"[^\"]*\"/\"AssemblyVersion\": \"$$NEW_VER\"/" $(YAML_NAME) && rm -f $(YAML_NAME).bak; \
		echo "Version bumped to $$NEW_VER (synced to repo.json, $(JSON_NAME), $(YAML_NAME))"; \
	else \
		echo "jq not found — version not bumped"; \
	fi
	@mkdir -p dist
	@rm -f dist/$(PROJECT_NAME).zip
	@cd $(BUILD_DIR) && \
		zip -q ../../dist/$(PROJECT_NAME).zip $(DLL_NAME) && \
		([ -f $(DLL_NAME:.dll=.deps.json) ] && zip -q ../../dist/$(PROJECT_NAME).zip $(DLL_NAME:.dll=.deps.json) || echo "Warning: deps.json not found") && \
		cd ../.. && \
		zip -q dist/$(PROJECT_NAME).zip $(JSON_NAME) $(YAML_NAME)
	@echo "Package: dist/$(PROJECT_NAME).zip"
	@ls -lh dist/$(PROJECT_NAME).zip

help:
	@echo "Targets: build | build-only | deploy | package [RELEASE_TAG=v1.x.x] | clean | rebuild"
	@echo "Plugin installs to: $(PLUGIN_DIR)"
