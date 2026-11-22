// Fluent System Icons (MIT), the set Windows 11 apps use. Only the ones the page shows are bundled.
import code from '@fluentui/svg-icons/icons/code_20_regular.svg?raw';
import storeMicrosoft from '@fluentui/svg-icons/icons/store_microsoft_20_regular.svg?raw';
import chevronRight from '@fluentui/svg-icons/icons/chevron_right_20_regular.svg?raw';
import checkmark from '@fluentui/svg-icons/icons/checkmark_20_regular.svg?raw';

export const icons = {
  code,
  storeMicrosoft,
  chevronRight,
  checkmark,
};

export type IconName = keyof typeof icons;
