# SPEC-0005 — tflint (UT-01): ruleset nativo do Terraform/OpenTofu, preset recomendado.
config {
  call_module_type = "local"
}

plugin "terraform" {
  enabled = true
  preset  = "recommended"
}
