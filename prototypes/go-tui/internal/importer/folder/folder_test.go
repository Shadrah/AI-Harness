package folder

import (
	"context"
	"os"
	"path/filepath"
	"testing"

	"github.com/will/harness/internal/importer"
)

func TestScanFindsContextAndExcludesSecrets(t *testing.T) {
	root := t.TempDir()
	files := map[string]string{
		"AGENTS.md":              "instructions",
		"README.md":              "context",
		"context/design.md":      "design",
		"context/token.md":       "do not import",
		"node_modules/README.md": "ignored",
	}
	for name, content := range files {
		path := filepath.Join(root, filepath.FromSlash(name))
		if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, []byte(content), 0o600); err != nil {
			t.Fatal(err)
		}
	}

	imp := Importer{}
	inventory, err := imp.Scan(context.Background(), importer.Source{Path: root})
	if err != nil {
		t.Fatal(err)
	}
	plan, err := imp.Preview(context.Background(), inventory)
	if err != nil {
		t.Fatal(err)
	}

	var foundInstruction, foundContext, skippedSecret bool
	for _, item := range plan.Items {
		switch item.Item.Label {
		case "AGENTS.md":
			foundInstruction = item.Item.Kind == importer.KindInstruction
		case filepath.FromSlash("context/design.md"):
			foundContext = item.Item.Kind == importer.KindContext
		case filepath.FromSlash("context/token.md"):
			skippedSecret = item.Action == importer.ActionSkip
		case filepath.FromSlash("node_modules/README.md"):
			t.Fatal("ignored directory was scanned")
		}
	}
	if !foundInstruction || !foundContext || !skippedSecret {
		t.Fatalf("unexpected plan: %#v", plan.Items)
	}
}
