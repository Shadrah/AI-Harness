package cli

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"mime"
	"os"
	"path/filepath"

	"github.com/will/harness/internal/app"
	"github.com/will/harness/internal/domain"
	"github.com/will/harness/internal/importer"
	"github.com/will/harness/internal/importer/folder"
	"github.com/will/harness/internal/provider/codex"
	"github.com/will/harness/internal/provider/codex/appserver"
)

func Run(args []string, stdout, stderr io.Writer) int {
	if len(args) == 0 {
		if err := app.Run(); err != nil {
			fmt.Fprintln(stderr, err)
			return 1
		}
		return 0
	}

	switch args[0] {
	case "doctor":
		return doctor(stdout, stderr)
	case "codex-models":
		return codexModels(stdout, stderr)
	case "codex-encode":
		return codexEncode(args[1:], stdout, stderr)
	case "import":
		if len(args) != 2 {
			fmt.Fprintln(stderr, "usage: harness import <folder>")
			return 2
		}
		return previewImport(args[1], stdout, stderr)
	case "help", "--help", "-h":
		fmt.Fprintln(stdout, "usage: harness [doctor | codex-models | codex-encode [flags] <prompt> | import <folder>]")
		return 0
	default:
		fmt.Fprintf(stderr, "unknown command %q\n", args[0])
		return 2
	}
}

func codexEncode(args []string, stdout, stderr io.Writer) int {
	flags := flag.NewFlagSet("codex-encode", flag.ContinueOnError)
	flags.SetOutput(stderr)
	imagePath := flags.String("image", "", "local image to attach")
	detail := flags.String("detail", "auto", "image detail: auto, low, high, or original")
	modelID := flags.String("model", "", "Codex model ID; defaults to the account default")
	if err := flags.Parse(args); err != nil {
		return 2
	}
	if flags.NArg() != 1 {
		fmt.Fprintln(stderr, "usage: harness codex-encode [--image path] [--detail level] [--model id] <prompt>")
		return 2
	}

	profiles, err := codex.New().LiveModels(context.Background())
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 1
	}
	if len(profiles) == 0 {
		fmt.Fprintln(stderr, "Codex app-server returned no models")
		return 1
	}
	profile := profiles[0]
	if *modelID != "" {
		found := false
		for _, candidate := range profiles {
			if candidate.Model == *modelID {
				profile, found = candidate, true
				break
			}
		}
		if !found {
			fmt.Fprintf(stderr, "model %q is not available\n", *modelID)
			return 1
		}
	}

	content := []domain.ContentBlock{domain.Text(flags.Arg(0))}
	if *imagePath != "" {
		absolute, err := filepath.Abs(*imagePath)
		if err != nil {
			fmt.Fprintln(stderr, err)
			return 1
		}
		info, err := os.Stat(absolute)
		if err != nil {
			fmt.Fprintln(stderr, err)
			return 1
		}
		content = append(content, domain.Image(domain.Resource{
			Source: domain.SourcePath, Value: absolute, MIMEType: mime.TypeByExtension(filepath.Ext(absolute)),
			Name: filepath.Base(absolute), Bytes: info.Size(),
		}, domain.ImageDetail(*detail)))
	}
	inputs, err := appserver.UserInputs(profile, content)
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 1
	}
	return writeJSON(stdout, stderr, struct {
		Model string                `json:"model"`
		Input []appserver.UserInput `json:"input"`
	}{Model: profile.Model, Input: inputs})
}

func codexModels(stdout, stderr io.Writer) int {
	profiles, err := codex.New().LiveModels(context.Background())
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 1
	}
	return writeJSON(stdout, stderr, profiles)
}

func doctor(stdout, stderr io.Writer) int {
	ctx := context.Background()
	status, err := codex.New().Detect(ctx)
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 1
	}
	profiles, err := codex.New().Models(ctx)
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 1
	}
	result := struct {
		Codex  any `json:"codex"`
		Models any `json:"models"`
	}{status, profiles}
	return writeJSON(stdout, stderr, result)
}

func previewImport(path string, stdout, stderr io.Writer) int {
	ctx := context.Background()
	absolute, err := filepath.Abs(path)
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 1
	}
	imp := folder.Importer{}
	inventory, err := imp.Scan(ctx, importer.Source{Path: absolute})
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 1
	}
	plan, err := imp.Preview(ctx, inventory)
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 1
	}
	return writeJSON(stdout, stderr, plan)
}

func writeJSON(stdout, stderr io.Writer, value any) int {
	encoder := json.NewEncoder(stdout)
	encoder.SetIndent("", "  ")
	if err := encoder.Encode(value); err != nil {
		fmt.Fprintln(stderr, err)
		return 1
	}
	return 0
}

func Main() {
	os.Exit(Run(os.Args[1:], os.Stdout, os.Stderr))
}
