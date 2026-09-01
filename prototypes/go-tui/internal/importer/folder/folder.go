package folder

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"time"

	"github.com/will/harness/internal/importer"
)

type Importer struct{}

func (Importer) ID() string { return "folder" }

func (Importer) Detect(context.Context) ([]importer.Source, error) { return nil, nil }

var instructionNames = []string{
	"AGENTS.md", "CLAUDE.md", ".cursorrules", "CONVENTIONS.md",
}

var contextNames = []string{
	"README.md", "PROJECT.md", "ARCHITECTURE.md", "CONTRIBUTING.md",
}

var ignoredDirectories = map[string]bool{
	".git": true, ".hg": true, ".svn": true, "node_modules": true,
	"vendor": true, "dist": true, "build": true, ".next": true,
}

var sensitiveFragments = []string{
	".env", "credential", "token", "cookie", "secret", "auth.json", "keychain",
}

func (Importer) Scan(_ context.Context, source importer.Source) (importer.Inventory, error) {
	root, err := filepath.Abs(source.Path)
	if err != nil {
		return importer.Inventory{}, err
	}
	info, err := os.Stat(root)
	if err != nil {
		return importer.Inventory{}, err
	}
	if !info.IsDir() {
		return importer.Inventory{}, fmt.Errorf("import source is not a directory: %s", root)
	}

	source.Path = root
	if source.ImporterID == "" {
		source.ImporterID = "folder"
	}
	if source.Harness == "" {
		source.Harness = "generic-folder"
	}
	if source.Label == "" {
		source.Label = filepath.Base(root)
	}

	items := []importer.Item{{
		ID:       hashID("project", root),
		Kind:     importer.KindProject,
		Label:    source.Label,
		Path:     root,
		Modified: info.ModTime(),
		Selected: true,
	}}

	err = filepath.WalkDir(root, func(path string, entry fs.DirEntry, walkErr error) error {
		if walkErr != nil {
			return walkErr
		}
		if entry.IsDir() {
			if path != root && ignoredDirectories[strings.ToLower(entry.Name())] {
				return filepath.SkipDir
			}
			return nil
		}

		relative, err := filepath.Rel(root, path)
		if err != nil {
			return err
		}
		if strings.Count(filepath.ToSlash(relative), "/") > 3 {
			return nil
		}

		name := entry.Name()
		kind := importer.Kind("")
		switch {
		case slices.Contains(instructionNames, name):
			kind = importer.KindInstruction
		case slices.Contains(contextNames, name):
			kind = importer.KindContext
		case strings.EqualFold(filepath.Ext(name), ".md") && strings.Contains(strings.ToLower(relative), "context"):
			kind = importer.KindContext
		default:
			return nil
		}

		fileInfo, err := entry.Info()
		if err != nil {
			return err
		}
		sensitive := looksSensitive(relative)
		items = append(items, importer.Item{
			ID:        hashID(string(kind), path),
			Kind:      kind,
			Label:     relative,
			Path:      path,
			Bytes:     fileInfo.Size(),
			Modified:  fileInfo.ModTime(),
			Sensitive: sensitive,
			Selected:  !sensitive,
			Reason:    sensitiveReason(sensitive),
		})
		return nil
	})
	if err != nil {
		return importer.Inventory{}, err
	}
	return importer.Inventory{Source: source, Items: items}, nil
}

func (Importer) Preview(_ context.Context, inventory importer.Inventory) (importer.Plan, error) {
	plan := importer.Plan{Source: inventory.Source, CreatedAt: time.Now().UTC()}
	for _, item := range inventory.Items {
		action := importer.ActionCopy
		if item.Kind == importer.KindProject {
			action = importer.ActionReference
		}
		if !item.Selected || item.Sensitive {
			action = importer.ActionSkip
			if item.Sensitive {
				plan.Warnings = append(plan.Warnings, fmt.Sprintf("excluded potentially sensitive item %s", item.Label))
			}
		}
		plan.Items = append(plan.Items, importer.PlannedItem{Item: item, Action: action})
	}
	return plan, nil
}

func (Importer) Import(ctx context.Context, plan importer.Plan, sink importer.Sink) error {
	for _, item := range plan.Items {
		if item.Action == importer.ActionSkip {
			continue
		}
		if err := sink.Put(ctx, item); err != nil {
			return fmt.Errorf("import %s: %w", item.Item.Label, err)
		}
	}
	return nil
}

func looksSensitive(path string) bool {
	lower := strings.ToLower(filepath.ToSlash(path))
	for _, fragment := range sensitiveFragments {
		if strings.Contains(lower, fragment) {
			return true
		}
	}
	return false
}

func sensitiveReason(sensitive bool) string {
	if sensitive {
		return "excluded because the path may contain credentials or authentication data"
	}
	return ""
}

func hashID(kind, value string) string {
	sum := sha256.Sum256([]byte(kind + "\x00" + value))
	return hex.EncodeToString(sum[:8])
}
