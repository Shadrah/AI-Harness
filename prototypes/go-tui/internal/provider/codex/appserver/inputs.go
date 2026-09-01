package appserver

import (
	"fmt"

	"github.com/will/harness/internal/domain"
)

func UserInputs(profile domain.ModelProfile, content []domain.ContentBlock) ([]UserInput, error) {
	if issues := domain.ValidateContent(profile, content); len(issues) > 0 {
		return nil, fmt.Errorf("content is not supported by %s: %s", profile.DisplayName, issues[0].Message)
	}
	inputs := make([]UserInput, 0, len(content))
	for _, block := range content {
		switch block.Kind {
		case domain.ContentText:
			inputs = append(inputs, UserInput{Type: "text", Text: block.Text})
		case domain.ContentImage:
			input := UserInput{Detail: string(block.ImageDetail)}
			switch block.Resource.Source {
			case domain.SourcePath:
				input.Type = "localImage"
				input.Path = block.Resource.Value
			case domain.SourceURL:
				input.Type = "image"
				input.URL = block.Resource.Value
			default:
				return nil, fmt.Errorf("Codex does not support image source %q through app-server", block.Resource.Source)
			}
			inputs = append(inputs, input)
		default:
			return nil, fmt.Errorf("Codex app-server encoder does not yet support content kind %q", block.Kind)
		}
	}
	return inputs, nil
}
