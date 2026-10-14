import type { SVGProps } from "react";

// Ícones decorativos (traço 2px, 24x24): sempre aria-hidden; o significado vem do texto ao lado.
function Icon({ children, ...props }: SVGProps<SVGSVGElement>) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={2}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
      {...props}
    >
      {children}
    </svg>
  );
}

export const ArrowRightIcon = (props: SVGProps<SVGSVGElement>) => (
  <Icon {...props}>
    <path d="M5 12h14M13 6l6 6-6 6" />
  </Icon>
);

export const AlertIcon = (props: SVGProps<SVGSVGElement>) => (
  <Icon {...props}>
    <circle cx="12" cy="12" r="9" />
    <path d="M12 8v5M12 16h.01" />
  </Icon>
);

export const InfoIcon = (props: SVGProps<SVGSVGElement>) => (
  <Icon {...props}>
    <circle cx="12" cy="12" r="9" />
    <path d="M12 11v5M12 8h.01" />
  </Icon>
);

export const EyeIcon = (props: SVGProps<SVGSVGElement>) => (
  <Icon {...props}>
    <path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z" />
    <circle cx="12" cy="12" r="3" />
  </Icon>
);

export const EyeOffIcon = (props: SVGProps<SVGSVGElement>) => (
  <Icon {...props}>
    <path d="M3 3l18 18M10.6 10.6a3 3 0 0 0 4.2 4.2M9.9 5.1A10.8 10.8 0 0 1 12 5c6.5 0 10 7 10 7a17 17 0 0 1-3.2 4.2M6.6 6.6C3.8 8.4 2 12 2 12s3.5 7 10 7a10 10 0 0 0 5.4-1.6" />
  </Icon>
);

export const LockIcon = (props: SVGProps<SVGSVGElement>) => (
  <Icon {...props}>
    <rect x="5" y="11" width="14" height="10" rx="2" />
    <path d="M8 11V7a4 4 0 0 1 8 0v4" />
  </Icon>
);

export const StoreIcon = (props: SVGProps<SVGSVGElement>) => (
  <Icon {...props}>
    <path d="M4 9h16l-1.5-5h-13zM4 9v1a2.7 2.7 0 0 0 5.3 0 2.7 2.7 0 0 0 5.4 0 2.7 2.7 0 0 0 5.3 0V9M5 12v8h14v-8" />
  </Icon>
);

export const SparklesIcon = (props: SVGProps<SVGSVGElement>) => (
  <Icon {...props}>
    <path d="M10 4l1.6 4.4L16 10l-4.4 1.6L10 16l-1.6-4.4L4 10l4.4-1.6zM18 14l.8 2.2L21 17l-2.2.8L18 20l-.8-2.2L15 17l2.2-.8zM18 2l.6 1.4L20 4l-1.4.6L18 6l-.6-1.4L16 4l1.4-.6z" />
  </Icon>
);

export const ChatIcon = (props: SVGProps<SVGSVGElement>) => (
  <Icon {...props}>
    <path d="M4 5h16v11H9l-5 4z" />
  </Icon>
);

export const DashboardIcon = (props: SVGProps<SVGSVGElement>) => (
  <Icon {...props}>
    <rect x="4" y="4" width="7" height="7" rx="1" />
    <rect x="13" y="4" width="7" height="4" rx="1" />
    <rect x="13" y="10" width="7" height="10" rx="1" />
    <rect x="4" y="13" width="7" height="7" rx="1" />
  </Icon>
);

export const ListingsIcon = (props: SVGProps<SVGSVGElement>) => (
  <Icon {...props}>
    <rect x="3" y="4" width="18" height="5" rx="1" />
    <path d="M5 9v10h14V9M10 13h4" />
  </Icon>
);
