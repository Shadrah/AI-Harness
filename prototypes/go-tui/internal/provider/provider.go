package provider

import (
	"context"

	"github.com/will/harness/internal/domain"
)

type Status struct {
	Available     bool   `json:"available"`
	Authenticated bool   `json:"authenticated"`
	Version       string `json:"version,omitempty"`
	Message       string `json:"message,omitempty"`
}

type Request struct {
	ProjectPath  string                `json:"project_path"`
	ThreadID     string                `json:"thread_id,omitempty"`
	Model        string                `json:"model"`
	Content      []domain.ContentBlock `json:"content"`
	Requirements []domain.Requirement  `json:"requirements,omitempty"`
}

type Runtime interface {
	ID() string
	Detect(ctx context.Context) (Status, error)
	Models(ctx context.Context) ([]domain.ModelProfile, error)
	Run(ctx context.Context, request Request) (<-chan domain.Event, error)
	Cancel(ctx context.Context, threadID string) error
}
